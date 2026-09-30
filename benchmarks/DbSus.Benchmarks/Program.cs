using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// In-process toolchain: the benchmark runs in this process instead of a generated project, which
// keeps the repository's build settings (signing, analyzers as errors) out of the way.
// Usage: dotnet run -c Release --project benchmarks/DbSus.Benchmarks -- --filter "*"
var config = DefaultConfig.Instance.AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
