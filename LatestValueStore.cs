using System;
using System.Collections.Concurrent;

namespace IndustrialBridge
{
    // This is the "Buffer" pattern industrial engineers use.
    // It decouples the crashing DA server from the UA server.
    public class LatestValueStore
    {
        private readonly ConcurrentDictionary<string, object> _store = new ConcurrentDictionary<string, object>();

        public void Update(string tag, object value)
        {
            _store[tag] = value;
            // Console.WriteLine($"[Store] Updated {tag}: {value}"); // Uncomment for debug
        }

        public object Get(string tag)
        {
            _store.TryGetValue(tag, out var val);
            return val;
        }

        public System.Collections.Generic.IEnumerable<string> GetAllTags()
        {
            return _store.Keys;
        }
    }
}