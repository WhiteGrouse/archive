using System;
using Newtonsoft.Json;

namespace LobiArchiver.Lobi
{
    public class Group
    {
        public string Icon { get; init; }
        public string Wallpaper { get; init; }
        public string Uid { get; init; }
        public IEnumerable<User> Members { get; init; }

        [JsonProperty("members_count")]
        public long? MembersCount { get; init; }

        [JsonProperty("members_next_cursor")]
        public string MembersNextCursor { get; init; }
        public User Owner { get; init; }
        public IEnumerable<User> Subleaders { get; init; }
    }

    public class Bookmarks
    {
        public IEnumerable<Chat> Data { get; init; }

        [JsonProperty("next_cursor")]
        public string NextCursor { get; init; }
    }
}

