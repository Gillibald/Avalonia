namespace Avalonia.Media.Imaging
{
    /// <summary>
    /// How <see cref="BitmapDecoder.TryIdentify(System.IO.Stream, out BitmapImageInfo, IdentifyStreamBehavior)"/>
    /// treats a forward-only stream.
    /// </summary>
    public enum IdentifyStreamBehavior
    {
        /// <summary>
        /// The stream must be seekable; its position is restored, so identify is
        /// repeatable and consumes nothing. A forward-only stream throws.
        /// </summary>
        RequireSeekable,

        /// <summary>
        /// A forward-only stream is identified by consuming a bounded prefix (at most
        /// the active backend's identify prefix length). Suited to sniff-only flows;
        /// the consumed bytes are gone, so decode afterwards through a decoder created
        /// from the (whole) stream instead. Seekable streams are still position-restored
        /// and never consumed.
        /// </summary>
        ConsumePrefix,
    }
}
