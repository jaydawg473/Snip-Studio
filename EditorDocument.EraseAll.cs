using System.Collections.Generic;

namespace SnipStudio
{
    public sealed partial class EditorDocument
    {
        // A single transaction shares the current image frame. Pixel edits and
        // crops belong to that frame and are not reverted by clearing overlays.
        public bool EraseAllAnnotations()
        {
            ThrowIfDisposed();
            if (!HasImage || Current.Annotations.Count == 0) return false;
            Current.Frame.AddReference();
            Commit(new DocumentState(Current.Frame, new List<Annotation>(), ++nextRevision));
            return true;
        }
    }
}
