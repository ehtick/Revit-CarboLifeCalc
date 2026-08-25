using System.Collections.Generic;

namespace CarboCircle.data
{
    /// <summary>
    /// What one finished import produced, and which side of the project asked for it.
    ///
    /// The side travels WITH the result, and that is the entire point of this class.
    ///
    /// It used to be remembered by the window instead: the window set a dataSwitch field before
    /// raising the external event, and read it back when the import completed. That is a race
    /// even with one window - the event is serviced later, and a second click in between moves
    /// the switch under the first import. With more than one window it was worse than a race,
    /// because every window ever opened answered the same event and each one applied its own
    /// switch to the same shared project. A mine could be parsed into the required bucket, and
    /// nothing anywhere would say so.
    ///
    /// Carrying the answer alongside the question removes the question of whose switch applies.
    /// </summary>
    internal class carboCircleImportResult
    {
        /// <summary>
        /// The elements collected. Null when the import failed - the handler has already told
        /// the user why, and the previous results are left on screen rather than wiped.
        /// </summary>
        public List<carboCircleElement> Elements { get; set; }

        /// <summary>
        /// True when this import was for the proposed design, false when it was for the mine.
        /// </summary>
        public bool ForProject { get; set; }

        public carboCircleImportResult(List<carboCircleElement> elements, bool forProject)
        {
            Elements = elements;
            ForProject = forProject;
        }
    }
}
