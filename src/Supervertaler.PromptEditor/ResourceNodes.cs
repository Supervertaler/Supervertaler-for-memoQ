namespace Supervertaler.PromptEditor
{
    /// <summary>
    /// What a tree node stands for, when it is not a prompt.
    ///
    /// <para>The tree began as the prompt library and is now the three things
    /// memoQ actually uses: prompts, memory banks and glossaries. The panel above
    /// it says which of each is active; the tree says what there is. Marker types
    /// rather than one node class with a kind field, so the selection handler
    /// switches on the type and a case that has not been written cannot silently
    /// fall through to the prompt editor.</para>
    /// </summary>
    internal sealed class BankNode
    {
        public string Name;
        public string Dir;
        public int Articles;
    }

    /// <summary>One Markdown article inside a memory bank.</summary>
    internal sealed class BankArticleNode
    {
        public string Bank;
        public string Path;
        public string Title;
    }

    /// <summary>A tab-separated glossary file.</summary>
    internal sealed class GlossaryNode
    {
        public string Path;
        public string Name;
    }

    /// <summary>A heading that owns one kind of resource. Selecting it edits nothing.</summary>
    internal sealed class SectionNode
    {
        public string Name;
        public override string ToString() => Name;
    }
}
