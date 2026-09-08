using System;

namespace Supervertaler.MemoQ.Core
{
    /// <summary>
    /// Lets the live document link say which project is open.
    ///
    /// <para>Until now the plugin learnt the project from one place: the
    /// <c>ProjectGuid</c> on a translation request. A project that never sends
    /// one – MT plugins disabled by the project manager, or simply not yet
    /// clicked into – left the panel naming the previous job, and a memory bank
    /// chosen then was recorded against it. The preview tool reports every
    /// document memoQ shows, whatever the MT settings say, and a document GUID
    /// resolves through <see cref="DocumentNames"/> to the project folder and its
    /// GUID. So a preview report is as good as a translation request for
    /// knowing where we are, and is acted on the same way.</para>
    ///
    /// <para>Cost: one dictionary lookup per report once a document has been
    /// resolved; the first sight of a document is one directory scan.</para>
    /// </summary>
    internal static class ProjectFollow
    {
        /// <summary>
        /// Notes that memoQ is showing <paramref name="documentGuid"/>. Returns
        /// what it resolved to, or null when no project folder holds the
        /// document – nothing is changed in that case.
        /// </summary>
        public static DocumentNames.Names Follow(Guid documentGuid, EngineContext context)
        {
            if (documentGuid == Guid.Empty) return null;

            var names = DocumentNames.Resolve(documentGuid);
            if (names == null || names.ProjectId == Guid.Empty) return names;

            if (context != null) context.NoteProject(names.ProjectId, names.Project);
            else EngineContext.RecordProject(names.ProjectId, names.Project);

            return names;
        }
    }
}
