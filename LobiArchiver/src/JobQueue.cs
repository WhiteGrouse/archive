using System;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace LobiArchiver
{
    public class JobQueue : IDisposable
    {
        private NpgsqlConnection _db;

        public JobQueue(string connectionString)
        {
            _db = new NpgsqlConnection(connectionString);
            _db.Open();

            using var command = _db.CreateCommand();

            //Create type
            command.CommandText = "SELECT count(*) FROM pg_type WHERE typname='job_state'";
            if ((long)command.ExecuteScalar()! == 0)
            {
                command.CommandText = "CREATE TYPE JOB_STATE AS ENUM ('QUEUED', 'FETCHING', 'COMPLETED')";
                command.ExecuteNonQuery();
            }

            //Create tables
            command.CommandText = "CREATE TABLE IF NOT EXISTS queue(id BIGSERIAL NOT NULL PRIMARY KEY, path VARCHAR(255) NOT NULL UNIQUE, state JOB_STATE NOT NULL DEFAULT 'QUEUED', cost INTEGER NOT NULL, code INTEGER, filename VARCHAR(255))";
            command.ExecuteNonQuery();
            command.CommandText = "CREATE TABLE IF NOT EXISTS asset(id BIGSERIAL NOT NULL PRIMARY KEY, url VARCHAR(255) NOT NULL UNIQUE)";
            command.ExecuteNonQuery();

            //Create index
            command.CommandText = "CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS queue_queued_cost_desc_id_asc_idx ON queue (cost DESC, id ASC) WHERE state='QUEUED'";
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            _db.Close();
            _db.Dispose();
        }

        public void Enqueue(string path, int cost) => EnqueueAll(new[] { path }, new[] { cost });

        public void EnqueueAll(IEnumerable<string> paths, IEnumerable<int> costs)
        {
            var columns = new[]
            {
                ("path", NpgsqlDbType.Varchar),
                ("cost", NpgsqlDbType.Integer),
            };
            var values = paths.Zip(costs).Select(d => new object[] { d.First, d.Second });
            BulkInsert("queue", columns, values);
        }

        public void Requeue(Job job)
        {
            using var command = _db.CreateCommand();
            command.CommandText = $"UPDATE queue SET state='QUEUED' WHERE id={job.Id}";
            command.ExecuteNonQuery();
        }

        public void NotifyComplete(Job job, int statusCode, string saveName)
        {
            using var command = _db.CreateCommand();
            command.CommandText = $"UPDATE queue SET state='COMPLETED', code={statusCode}, filename=@filename WHERE id={job.Id}";
            command.Parameters.Add("@filename", NpgsqlDbType.Varchar);
            command.Prepare();
            command.Parameters["@filename"].Value = saveName;
            command.ExecuteNonQuery();
        }

        public void NotifyCompleteAll(IEnumerable<(Job, int)> results, string saveName)
        {
            using var command = _db.CreateCommand();
            command.CommandText = "UPDATE queue SET state='COMPLETED', code=@code, filename=@filename WHERE id=@id";
            command.Parameters.Add("@code", NpgsqlDbType.Integer);
            command.Parameters.Add("@filename", NpgsqlDbType.Varchar);
            command.Parameters.Add("@id", NpgsqlDbType.Bigint);
            command.Prepare();
            foreach (var (job, statusCode) in results)
            {
                command.Parameters["@code"].Value = statusCode;
                command.Parameters["@filename"].Value = saveName;
                command.Parameters["@id"].Value = job.Id;
                command.ExecuteNonQuery();
            }
        }

        public Job? Dequeue()
        {
            using var command = _db.CreateCommand();
            command.CommandText = "UPDATE queue SET state='FETCHING' WHERE id=(SELECT id FROM queue WHERE state='QUEUED' ORDER BY cost DESC, id ASC LIMIT 1) RETURNING id, path";
            using var reader = command.ExecuteReader();
            if (!reader.HasRows)
                return null;
            reader.Read();
            var id = reader.GetInt64(0);
            var path = reader.GetString(1);
            return new Job(id, path);
        }

        public IEnumerable<Job> DequeueN(int n)
        {
            using var command = _db.CreateCommand();
            command.CommandText = $"UPDATE queue SET state='FETCHING' WHERE id IN (SELECT id FROM queue WHERE state='QUEUED' ORDER BY cost DESC, id ASC LIMIT {n}) RETURNING id, path";
            using var reader = command.ExecuteReader();
            if (!reader.HasRows)
                yield break;
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var path = reader.GetString(1);
                yield return new Job(id, path);
            }
        }

        public void AddAsset(string? url) => AddAssetAll(new[] { url });

        public void AddAssetAll(IEnumerable<string?> urls)
        {
            var columns = new[] { ("url", NpgsqlDbType.Varchar) };
            var values = urls.Where(d => !string.IsNullOrEmpty(d)).Select(d => new[] { d! });
            BulkInsert("asset", columns, values);
        }

        private void BulkInsert(string table, IEnumerable<(string, NpgsqlDbType)> columns, IEnumerable<IEnumerable<object>> values)
        {
            int size = 80;
            var _columns = columns.ToList();
            var _values = values.Select(d => d.ToList()).ToList();
            if (_values.Count == 0)
                return;
            if (_values.Count > size)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = BuildBulkInsertSQL(table, _columns, size);
                AddBulkInsertParameters(cmd, size, _columns.Select(d => d.Item2));
                cmd.Prepare();
                for (int k = 0; k < _values.Count / size; k++)
                    ExecuteBulkInsert(cmd, _values.Skip(k * size).Take(size));
            }
            var remains = _values.TakeLast(_values.Count % size).ToList();
            if (remains.Count > 0)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = BuildBulkInsertSQL(table, _columns, remains.Count);
                AddBulkInsertParameters(cmd, remains.Count, _columns.Select(d => d.Item2));
                cmd.Prepare();
                ExecuteBulkInsert(cmd, remains);
            }
        }

        private void ExecuteBulkInsert(NpgsqlCommand command, IEnumerable<IEnumerable<object>> values)
        {
            while (true)
            {
                try
                {
                    var _values = values.Select(d => d.ToList()).ToList();
                    for (int i = 0; i < _values.Count; i++)
                        for (int j = 0; j < _values[i].Count; j++)
                            command.Parameters[$"r{i}c{j}"].Value = _values[i][j];
                    command.ExecuteNonQuery();
                    break;
                }
                catch (PostgresException ex)
                {
                    if (ex.SqlState != "40P01")
                        throw;
                    int interval = Random.Shared.Next(10, 30);
                    Console.WriteLine($"Detected SQL deadlock. Retry after {interval}ms...");
                    Task.Delay(interval).Wait();
                }
            }
        }

        private void AddBulkInsertParameters(NpgsqlCommand command, int size, IEnumerable<NpgsqlDbType> columns)
        {
            var _columns = columns.ToList();
            for (int i = 0; i < size; i++)
                for (int j = 0; j < _columns.Count; j++)
                    command.Parameters.Add($"@r{i}c{j}", _columns[j]);
        }

        private string BuildBulkInsertSQL(string table, IEnumerable<(string, NpgsqlDbType)> columns, int size)
        {
            var _columns = columns.ToList();
            var builder = new StringBuilder();
            builder.Append($"INSERT INTO {table} (");
            builder.AppendJoin(", ", _columns.Select(d => d.Item1));
            builder.Append(") VALUES (@r0c0");
            for (int j = 1; j < _columns.Count; j++)
                builder.Append($", @r0c{j}");
            builder.Append(")");
            for (int i = 1; i < size; i++)
            {
                builder.Append($", (@r{i}c0");
                for (int j = 1; j < _columns.Count; j++)
                    builder.Append($", @r{i}c{j}");
                builder.Append(")");
            }
            builder.Append(" ON CONFLICT DO NOTHING");
            return builder.ToString();
        }
    }

    public record Job(long Id, string Path);
}

