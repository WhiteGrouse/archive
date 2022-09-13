using System;
using System.IO;
using System.Text;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using System.Diagnostics;

namespace LobiArchiver
{
    public class Archiver
    {
        private const string ConnectionString = "Host=db;Port=5432;Username=archiver;Password=archiver;Database=lobi";

        private const int BatchSize = 140;
        private const int NumOfMeasurements = 100;

        private bool abortRequested = false;

        public void Abort()
        {
            abortRequested = true;
            Console.WriteLine("Abort requested");
        }

        public async Task Run()
        {
            // storage margin=1%
            var storage = new Storage(DriveInfo.GetDrives()
                .Where(drive => drive.DriveFormat == "fuse" && drive.RootDirectory.FullName != "/app/groups")
                .Select(drive => Volume.Get(drive.RootDirectory.FullName, "Archive", drive.TotalSize / 100)));
            //var storage = new Storage(new[]
            //{
            //    //MountPoint, Directory
            //    //Volume.Get("C:", "Users\hoge\Desktop\Lobi"),//未検証
            //    Volume.Get("/mnt/Archive4", "Archive4", 1L * 1024 * 1024 * 1024),//margin=1GB
            //    Volume.Get("/mnt/Archive5", "Archive5", 1L * 1024 * 1024 * 1024),//margin=1GB
            //    Volume.Get("/", "root/Archive1", 20L * 1024 * 1024 * 1024),//margin=20GB
            //    Volume.Get("/mnt/Archive2", "Archive2", 1L * 1024 * 1024 * 1024),//margin=1GB
            //    Volume.Get("/mnt/Archive3", "Archive3", 1L * 1024 * 1024 * 1024),//margin=1GB
            //});
            Console.WriteLine("Storage: ");
            foreach (var volume in storage.Volumes) {
                Console.WriteLine($"- {volume.Drive.RootDirectory.FullName}");
            }
            Console.WriteLine();

            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
            using var client = new HttpClient(handler);
            client.BaseAddress = new Uri("https://web.lobi.co/");
            client.DefaultRequestVersion = HttpVersion.Version11;
            client.Timeout = TimeSpan.FromSeconds(8);

            using var queue = new JobQueue(ConnectionString);

            if (File.Exists("/app/groups"))
            {
                Console.WriteLine("\"groups\" file found. Queueing...");
                using var reader = new StreamReader("/app/groups", Encoding.ASCII);
                while (!reader.EndOfStream)
                {
                    var uid = reader.ReadLine();
                    if (string.IsNullOrEmpty(uid))
                        break;
                    var (path, cost) = BuildPath_GroupInfo(uid);
                    queue.Enqueue(path, cost);
                }
                Console.WriteLine("Group list queued.");
            }
            else
            {
                Console.WriteLine("Resume...");
            }

            var sw_loop = new Stopwatch();
            var sw_dequeue = new Stopwatch();
            var sw_request = new Stopwatch();
            var sw_save = new Stopwatch();
            var sw_handle = new Stopwatch();
            int sw_i = 0;
            int sw_total_req = 0;

            while (!abortRequested)
            {
                sw_loop.Start();
                sw_dequeue.Start();
                var requests = queue.DequeueN(BatchSize).ToList();
                sw_dequeue.Stop();
                if (requests.Count == 0)
                    break;
                sw_total_req += requests.Count;

                sw_request.Start();
                var results = await Task.WhenAll(requests.Select(d => Get(client, d.Path)));
                sw_request.Stop();

                sw_save.Start();
                var filename = $"packed_{requests[0].Id}";
                using (var buffer = new MemoryStream())
                using (var writer = new BinaryWriter(buffer))
                {
                    writer.Write((int)100);//version
                    for (int i = 0; i < results.Length; i++)
                    {
                        var job = requests[i];
                        var (code, body) = results[i];

                        if (code != HttpStatusCode.OK)
                            continue;

                        writer.Write(job.Id);
                        writer.Write(body.Length);
                        writer.Write(body);
                    }
                    if (!storage.Save(filename, buffer.ToArray()))
                    {
                        var ex = new IOException($"Failed to save packed_{requests[0].Id}");
                        ex.Data.Add("requests", requests);
                        ex.Data.Add("results", results);
                        foreach (var job in requests)
                            queue.Requeue(job);

                        throw ex;
                        //ここで死んだら念の為ファイルが存在していないか検索して、存在していたら削除して以下のSQLを実行した上でResume
                        //UPDATE queue SET state='QUEUED' WHERE state='FETCHING';
                    }
                }
                sw_save.Stop();

                //ここから先で死んだらRollbackerを実行してResume

                sw_handle.Start();
                var notify_list = new (Job, int)?[results.Length];
                var jobs_list = new List<(string, int)>[results.Length];
                var assets_list = new List<string?>[results.Length];
                Parallel.For(0, results.Length, i =>
                {
                    jobs_list[i] = new List<(string, int)>();
                    assets_list[i] = new List<string?>();

                    var job = requests[i];
                    var (code, body) = results[i];

                    if (code != HttpStatusCode.OK)
                    {
                        notify_list[i] = (job, (int)code);
                        return;
                    }

                    var content = Encoding.UTF8.GetString(body);

                    var groupInfo = Regex.Match(job.Path, @"^api/group/([0-9a-f]{40})\?fields");
                    var groupMembers = Regex.Match(job.Path, @"^api/group/([0-9a-f]{40})\?members_cursor");
                    var groupBookmarks = Regex.Match(job.Path, @"^api/group/([0-9a-f]{40})/bookmarks");
                    var groupThreads = Regex.Match(job.Path, @"^api/group/([0-9a-f]{40})/chats\?");
                    var groupReplies = Regex.Match(job.Path, @"^api/group/([0-9a-f]{40})/chats/replies");
                    var userInfo = Regex.Match(job.Path, @"^api/user/([0-9a-f]{40})\?fields");
                    var userContactsOrFollowers = Regex.Match(job.Path, @"^api/user/([0-9a-f]{40})/");

                    try
                    {
                        if (groupInfo.Success)
                            HandleGroupInfo(content, jobs_list[i], assets_list[i]);
                        else if (groupMembers.Success)
                            HandleGroupMembers(content, jobs_list[i], assets_list[i]);
                        else if (groupBookmarks.Success)
                            HandleGroupBookmarks(content, jobs_list[i], assets_list[i], groupBookmarks.Groups[1].Value);
                        else if (groupThreads.Success)
                            HandleGroupThreads(content, jobs_list[i], assets_list[i], groupThreads.Groups[1].Value);
                        else if (groupReplies.Success)
                            HandleGroupReplies(content, jobs_list[i], assets_list[i]);
                        else if (userInfo.Success)
                            HandleUserInfo(content, jobs_list[i], assets_list[i]);
                        // ユーザを再帰的に取得したい場合はコメントアウトを外す。
                        // 最悪Lobiのユーザ数百万人分を取得することになるので注意。
                        //else if (userContactsOrFollowers.Success)
                        //    HandleUserContactsOrFollowers(content, jobs_list[i], assets_list[i]);
                    }
                    catch (JsonException)
                    {
                        Console.WriteLine("Detected server rebooting. This job is requeued.");
                        notify_list[i] = null;
                        return;
                    }

                    notify_list[i] = (job, (int)code);
                });
                queue.EnqueueAll(jobs_list.SelectMany(jobs => jobs.Select(d => d.Item1)), jobs_list.SelectMany(jobs => jobs.Select(d => d.Item2)));
                queue.AddAssetAll(assets_list.SelectMany(d => d));
                queue.NotifyCompleteAll(notify_list.Where(d => d != null).OfType<(Job, int)>(), filename);
                for (int i = 0; i < results.Length; i++)
                    if (notify_list[i] == null)
                        queue.Requeue(requests[i]);
                sw_handle.Stop();
                sw_loop.Stop();

                if (++sw_i >= NumOfMeasurements)
                {
                    Console.WriteLine($"Request Speed: {sw_request.ElapsedMilliseconds / (double)sw_total_req} ms/req");
                    Console.WriteLine($"Loop Time    : {sw_loop.ElapsedMilliseconds / (double)NumOfMeasurements} ms/loop");
                    Console.WriteLine($"- Request    : {sw_request.ElapsedMilliseconds / (double)NumOfMeasurements} ms/loop");
                    Console.WriteLine($"- Dequeue    : {sw_dequeue.ElapsedMilliseconds / (double)NumOfMeasurements} ms/loop");
                    Console.WriteLine($"- Save       : {sw_save.ElapsedMilliseconds / (double)NumOfMeasurements} ms/loop");
                    Console.WriteLine($"- Handle     : {sw_handle.ElapsedMilliseconds / (double)NumOfMeasurements} ms/loop");
                    Console.WriteLine();
                    sw_loop.Reset();
                    sw_dequeue.Reset();
                    sw_request.Reset();
                    sw_save.Reset();
                    sw_handle.Reset();
                    sw_i = 0;
                    sw_total_req = 0;
                }
            }
            if (abortRequested)
                Console.WriteLine("Aborted...");
            else
                Console.WriteLine("Mission completed...");
        }

        private void HandleGroupInfo(string content, List<(string, int)> jobs, List<string?> assets)
        {
            var group = JsonConvert.DeserializeObject<Lobi.Group>(content);

            assets.Add(group.Icon);
            assets.Add(group.Wallpaper);

            jobs.Add(BuildPath_GroupBookmarks(group.Uid));
            jobs.Add(BuildPath_GroupMembers(group.Uid));
            jobs.Add(BuildPath_GroupThreads(group.Uid));
            jobs.Add(BuildPath_UserInfo(group.Owner.Uid));
            jobs.Add(BuildPath_UserContacts(group.Owner.Uid));
            jobs.Add(BuildPath_UserFollowers(group.Owner.Uid));
            foreach (var subleader in group.Subleaders)
            {
                jobs.Add(BuildPath_UserInfo(subleader.Uid));
                jobs.Add(BuildPath_UserContacts(subleader.Uid));
                jobs.Add(BuildPath_UserFollowers(subleader.Uid));
            }
        }

        private void HandleGroupMembers(string content, List<(string, int)> jobs, List<string?> assets)
        {
            var group = JsonConvert.DeserializeObject<Lobi.Group>(content);

            if (!string.IsNullOrEmpty(group.MembersNextCursor) && group.MembersNextCursor != "0" && group.MembersNextCursor != "-1")
                jobs.Add(BuildPath_GroupMembers(group.Uid, group.MembersNextCursor));
            foreach (var user in group.Members)
            {
                jobs.Add(BuildPath_UserInfo(user.Uid));
                jobs.Add(BuildPath_UserContacts(user.Uid));
                jobs.Add(BuildPath_UserFollowers(user.Uid));
            }
        }

        private void HandleGroupBookmarks(string content, List<(string, int)> jobs, List<string?> assets, string group_uid)
        {
            var bookmarks = JsonConvert.DeserializeObject<Lobi.Bookmarks>(content);

            if (!string.IsNullOrEmpty(bookmarks.NextCursor) && bookmarks.NextCursor != "0" && bookmarks.NextCursor != "-1")
                jobs.Add(BuildPath_GroupBookmarks(group_uid, bookmarks.NextCursor));
            foreach (var chat in bookmarks.Data)
            {
                if (chat.Assets != null)
                    assets.AddRange(chat.Assets.SelectMany(d => new[] { d.Url, d.RawUrl }));
                jobs.Add(BuildPath_UserInfo(chat.User.Uid));
                jobs.Add(BuildPath_UserContacts(chat.User.Uid));
                jobs.Add(BuildPath_UserFollowers(chat.User.Uid));
            }
        }

        private void HandleGroupThreads(string content, List<(string, int)> jobs, List<string?> assets, string group_uid)
        {
            var threads = JsonConvert.DeserializeObject<List<Lobi.Chat>>(content);

            var last = threads!.LastOrDefault();
            if (last != null && last.Type != "system.created")
                jobs.Add(BuildPath_GroupThreads(group_uid, last.Id));
            foreach (var chat in threads)
            {
                if (chat.Assets != null)
                    assets.AddRange(chat.Assets.SelectMany(d => new[] { d.Url, d.RawUrl }));
                if (chat.Replies != null && chat.Replies.Count > 0)
                {
                    if (chat.Type == "normal" || chat.Type == "shout")
                        jobs.Add(BuildPath_GroupReplies(group_uid, chat.Id));
                    foreach (var reply in chat.Replies.Chats)
                    {
                        if (reply.Assets != null)
                            assets.AddRange(reply.Assets.SelectMany(d => new[] { d.Url, d.RawUrl }));
                        jobs.Add(BuildPath_UserInfo(reply.User.Uid));
                        jobs.Add(BuildPath_UserContacts(reply.User.Uid));
                        jobs.Add(BuildPath_UserFollowers(reply.User.Uid));
                    }
                }
                jobs.Add(BuildPath_UserInfo(chat.User.Uid));
                jobs.Add(BuildPath_UserContacts(chat.User.Uid));
                jobs.Add(BuildPath_UserFollowers(chat.User.Uid));
            }
        }

        private void HandleGroupReplies(string content, List<(string, int)> jobs, List<string?> assets)
        {
            var replies = JsonConvert.DeserializeObject<Lobi.Replies>(content);

            foreach (var chat in replies.Chats)
            {
                if (chat.Assets != null)
                    assets.AddRange(chat.Assets.SelectMany(d => new[] { d.Url, d.RawUrl }));
                jobs.Add(BuildPath_UserInfo(chat.User.Uid));
                jobs.Add(BuildPath_UserContacts(chat.User.Uid));
                jobs.Add(BuildPath_UserFollowers(chat.User.Uid));
            }
        }

        private void HandleUserInfo(string content, List<(string, int)> jobs, List<string?> assets)
        {
            var user = JsonConvert.DeserializeObject<Lobi.User>(content);

            assets.Add(user.Icon);
            assets.Add(user.Cover);
        }

        private void HandleUserContactsOrFollowers(string content, List<(string, int)> jobs, List<string?> assets)
        {
            var contacts = JsonConvert.DeserializeObject<Lobi.Contacts>(content);

            foreach (var user in contacts.Users)
            {
                jobs.Add(BuildPath_UserInfo(user.Uid));
                jobs.Add(BuildPath_UserContacts(user.Uid));
                jobs.Add(BuildPath_UserFollowers(user.Uid));
            }
        }

        private async Task<(HttpStatusCode, byte[])> Get(HttpClient client, string path)
        {
            while (true)
            {
                try
                {
                    using var res = await client.GetAsync(path);
                    var content = await res.Content.ReadAsByteArrayAsync();
                    if (res.StatusCode == HttpStatusCode.BadGateway)
                    {
                        Console.WriteLine("Detected server down. Retry after 3sec...");
                        await Task.Delay(3000);
                        continue;
                    }
                    return (res.StatusCode, content);
                }
                catch (TaskCanceledException)
                {
                    Console.WriteLine("Detected timeout. Retry after 1sec...");
                    await Task.Delay(1000);
                    continue;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error on Get(): Message={ex.Message}");
                    Console.WriteLine("Retry after 1sec...");
                    await Task.Delay(1000);
                    continue;
                }
            }
        }

        private (string, int) BuildPath_GroupInfo(string group_uid) =>
        ($"api/group/{group_uid}?fields=game_info,group_bookmark_info,subleaders,category,join_applications_count&count=1&members_count=0", 7);

        private (string, int) BuildPath_GroupMembers(string group_uid, string cursor = "0") =>
        ($"api/group/{group_uid}?members_cursor={cursor}&count=1&members_count=100", 6);

        private (string, int) BuildPath_GroupBookmarks(string group_uid, string cursor = "0") =>
        ($"api/group/{group_uid}/bookmarks?cursor={cursor}&count=100", 5);

        private (string, int) BuildPath_GroupThreads(string group_uid, string chat_uid = "0") =>
        ($"api/group/{group_uid}/chats?older_than={chat_uid}&count=30", 4);

        private (string, int) BuildPath_GroupReplies(string group_uid, string chat_uid) =>
        ($"api/group/{group_uid}/chats/replies?to={chat_uid}", 3);

        private (string, int) BuildPath_UserInfo(string user_uid) =>
        ($"api/user/{user_uid}?fields=premium", 1);

        private (string, int) BuildPath_UserContacts(string user_uid) =>
        ($"api/user/{user_uid}/contacts", 2);

        private (string, int) BuildPath_UserFollowers(string user_uid) =>
        ($"api/user/{user_uid}/followers", 2);
    }
}
