using System;
using System.Text;

namespace LobiArchiver
{
    public record Volume(DriveInfo Drive, string Directory, long MarginSize)
    {
        public static Volume Get(string driveName, string directory, long marginSize) => new Volume(new DriveInfo(driveName), directory, marginSize);

        public string RootPath => Path.Join(Drive.RootDirectory.FullName, Directory);
    }

    public class Storage
    {
        private const int Distribution = 200;

        private Volume[] _volumes;
        public IEnumerable<Volume> Volumes { get => _volumes; }

        private object lockObj = new object();
        private int _currentDrive = 0;
        private int _writing = 0;

        private Random random = new Random(Environment.TickCount);

        private class WritingHandle : IDisposable
        {
            public Storage Storage { get; init; }
            public Volume Volume { get; init; }
            public int Size { get; init; }

            public void Dispose()
            {
                lock (Storage.lockObj)
                {
                    if (Storage._volumes.ElementAtOrDefault(Storage._currentDrive)?.Drive.VolumeLabel == Volume.Drive.VolumeLabel)
                    {
                        Storage._writing -= Size;
                    }
                }
            }
        }

        public Storage(IEnumerable<Volume> volumes)
        {
            _volumes = volumes.ToArray();

            foreach (var volume in _volumes)
            {
                Directory.CreateDirectory(volume.RootPath);
                for (int i = 0; i < Distribution; i++)
                {
                    Directory.CreateDirectory(Path.Join(volume.RootPath, i.ToString()));
                }
            }
        }

        private WritingHandle? GetDriveToSave(int size)
        {
            lock (lockObj)
            {
                for (; _currentDrive < _volumes.Length; _currentDrive++, _writing = 0)
                {
                    var volume = _volumes[_currentDrive];
                    if ((volume.Drive.AvailableFreeSpace - _writing - size) > volume.MarginSize)
                    {
                        _writing += size;
                        return new WritingHandle
                        {
                            Storage = this,
                            Volume = volume,
                            Size = size,
                        };
                    }
                }
                return null;
            }
        }

        public bool Save(string key, byte[] content)
        {
            try
            {
                using var handle = GetDriveToSave(content.Length);
                if (handle == null)
                    return false;
                //var filename = Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).Replace('/', '_');
                var filename = key;
                var path = Path.Join(handle.Volume.RootPath, random.Next(0, Distribution).ToString(), filename);
                File.WriteAllBytes(path, content);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return false;
            }
        }
    }
}
