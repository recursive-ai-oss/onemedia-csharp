namespace RecursiveAI.OneMedia;

public static partial class Media
{
    /// <summary>
    /// Repairs a damaged file into a new file: rebuilds missing or broken indexes and durations, drops what was cut off, and copies media
    /// payload bytes unchanged. The input is never modified.
    /// </summary>
    /// <param name="input">The damaged file.</param>
    /// <param name="output">The new file; refused if it exists.</param>
    /// <param name="reference">A healthy file from the same device, for the reconstruction of truncated files that need one; optional.</param>
    /// <remarks>Core cannot frame AAC audio in a reconstruction. To recover AAC tracks of a cut-off recording use <c>AacReconstructionProbe.Repair</c> in <c>RecursiveAI.OneMedia.Audio</c>, <c>onemedia repair</c>, or <see cref="RecursiveAI.OneMedia.Repair.Repairer"/> with the probe's options.</remarks>
    /// <returns>What was rebuilt, dropped and kept, per structure and track, with a confidence. A healthy input is reported as <see cref="RecursiveAI.OneMedia.Repair.RepairStatus.NothingToRepair"/> and copied byte for byte.</returns>
    /// <exception cref="IOException">The output exists, or a file cannot be read or written.</exception>
    /// <exception cref="UnsupportedMediaException">The format is not recognised or has no repairer.</exception>
    public static RecursiveAI.OneMedia.Repair.RepairReport Repair(string input, string output, string? reference = null) => RecursiveAI.OneMedia.Repair.Repairer.Repair(input, output, reference);
}
