```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9974)
AMD Ryzen 9 3900X 3.80GHz, 1 CPU, 24 logical and 12 physical cores
.NET SDK 10.0.403
  [Host]   : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method          | Rows   | BatchSize | Mean             | Error            | StdDev        | Ratio  | RatioSD | Gen0       | Allocated   | Alloc Ratio |
|---------------- |------- |---------- |-----------------:|-----------------:|--------------:|-------:|--------:|-----------:|------------:|------------:|
| EqualTables     | 100000 | 1000      |  10,727,552.1 ns |    580,910.66 ns |  31,841.67 ns |  1.000 |    0.00 |          - |       816 B |        1.00 |
| AllRowsModified | 100000 | 1000      | 169,115,988.9 ns | 13,502,229.49 ns | 740,102.76 ns | 15.765 |    0.07 | 14333.3333 | 120004101 B |  147,063.85 |
| Fingerprint     | 100000 | 1000      |         667.4 ns |         83.98 ns |       4.60 ns |  0.000 |    0.00 |     0.0582 |       488 B |        0.60 |
