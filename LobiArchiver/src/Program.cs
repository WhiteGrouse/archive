using System;
using System.Net;

namespace LobiArchiver
{
    class Program
    {
        static void Main(string[] args)
        {
            var archiver = new Archiver();
            var task = archiver.Run();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                if (task.IsCompleted)
                    return;
                archiver.Abort();
                task.Wait();
            };
            task.Wait();
        }
    }
}
