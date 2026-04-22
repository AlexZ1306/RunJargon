namespace RunJargon.App.Services;

public sealed class TranslationTextCache : ITranslationTextCache
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lruKeys = new();

    public TranslationTextCache(int capacity = 512)
    {
        _capacity = Math.Max(2, capacity);
    }

    public bool TryGet(
        string? sourceLanguage,
        string targetLanguage,
        string normalizedSourceText,
        out string translatedText)
    {
        var key = BuildKey(sourceLanguage, targetLanguage, normalizedSourceText);

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                translatedText = string.Empty;
                return false;
            }

            MoveToFront(entry.Node);
            translatedText = entry.TranslatedText;
            return true;
        }
    }

    public void Set(
        string? sourceLanguage,
        string targetLanguage,
        string normalizedSourceText,
        string translatedText)
    {
        var key = BuildKey(sourceLanguage, targetLanguage, normalizedSourceText);

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.TranslatedText = translatedText;
                MoveToFront(existing.Node);
                return;
            }

            var node = new LinkedListNode<string>(key);
            _lruKeys.AddFirst(node);
            _entries[key] = new CacheEntry(node, translatedText);

            while (_entries.Count > _capacity)
            {
                var last = _lruKeys.Last;
                if (last is null)
                {
                    break;
                }

                _lruKeys.RemoveLast();
                _entries.Remove(last.Value);
            }
        }
    }

    private void MoveToFront(LinkedListNode<string> node)
    {
        _lruKeys.Remove(node);
        _lruKeys.AddFirst(node);
    }

    private static string BuildKey(string? sourceLanguage, string targetLanguage, string normalizedSourceText)
    {
        return $"{sourceLanguage ?? string.Empty}\u001F{targetLanguage}\u001F{normalizedSourceText}";
    }

    private sealed class CacheEntry
    {
        public CacheEntry(LinkedListNode<string> node, string translatedText)
        {
            Node = node;
            TranslatedText = translatedText;
        }

        public LinkedListNode<string> Node { get; }

        public string TranslatedText { get; set; }
    }
}
