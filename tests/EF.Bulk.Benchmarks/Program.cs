using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Entry point marker for <see cref="BenchmarkSwitcher" />.</summary>
public partial class Program;
