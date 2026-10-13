namespace SeleVox.Tests;

// Other audio tests must not contaminate the captured marker or change endpoint state.
[CollectionDefinition("Audio onset diagnostic", DisableParallelization = true)]
public sealed class AudioOnsetCollection;
