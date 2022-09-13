using System;
using Newtonsoft.Json;

namespace LobiArchiver.Lobi
{
    public class Chat
    {
        public string Id { get; init; }
        public User User { get; init; }
        public string Type { get; init; }
        public IEnumerable<Asset> Assets { get; init; }
        public Replies Replies { get; init; }
    }

    public class Asset
    {
        public string Id { get; init; }

        [JsonProperty("raw_url")]
        public string RawUrl { get; init; }
        public string Url { get; init; }
        public string Type { get; init; }
    }

    public class Replies
    {
        public int Count { get; init; }
        public IEnumerable<Chat> Chats { get; init; }
    }
}

