namespace SnipStudio
{
    public sealed partial class EditorCanvas
    {
        public int AnnotationCount { get { return document.AnnotationCount; } }

        public bool EraseAllAnnotations()
        {
            if (disposingCanvas || IsDisposed || !HasImage || !CommitInlineText()) return false;
            if (document.AnnotationCount == 0) return false;
            CancelGesture();
            CancelEmojiPlacement();
            CancelSourceStyleMatching();
            selectedIndex = -1;
            textSelection.Clear();
            textSelectionAnchor = -1;
            if (!document.EraseAllAnnotations()) return false;
            RaiseTextSelectionChanged();
            RaiseChanged();
            return true;
        }
    }
}
