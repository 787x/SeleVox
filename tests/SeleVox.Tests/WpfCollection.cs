namespace SeleVox.Tests;

// WPF's resource package cache is process-wide and not safe for concurrent STA dispatchers.
[CollectionDefinition("WPF desktop", DisableParallelization = true)]
public sealed class WpfCollection;
