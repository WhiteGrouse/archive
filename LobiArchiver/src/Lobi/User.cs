using System;

namespace LobiArchiver.Lobi
{
    public class User
    {
        public string Icon { get; init; }
        public string Cover { get; init; }
        public string Uid { get; init; }
    }

    public class Contacts
    {
        public int Visibility { get; init; }
        public IEnumerable<User> Users { get; init; }
    }
}

