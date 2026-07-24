using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Slopworks.App;
using Slopworks.App.ViewModels;
using Slopworks.App.Views;
using Slopworks.Core.Artifacts;
using Slopworks.Core.Config;
using Slopworks.Core.Logging;
using Slopworks.Core.Platform;
using Slopworks.Core.Server;
using Slopworks.Core.State;
using Slopworks.Screenshots;

// Renders each tab to a sanitized 1920x1260 PNG using a fake host (no real machine data, no
// usernames/paths). Run from the repo root: dotnet run --project tools/Slopworks.Screenshots [outDir]

var outDir = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "docs", "screenshots");
Directory.CreateDirectory(outDir);

// Username-free data root so any tab that shows a path stays clean.
var root = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "SlopworksShots");
if (Directory.Exists(root))
    Directory.Delete(root, recursive: true);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var host = BuildHost(root);
var vm = new MainWindowViewModel(host);
var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
window.Show();
Pump();

// (tabIndex, filename) — the changed + new tabs.
(int Tab, string File)[] shots =
[
    (0, "dashboard.png"),
    (2, "server.png"),
    (3, "system.png"),
    (4, "models.png"),
    (5, "settings.png"),
    (6, "platform.png"),
    (7, "templates.png"),
    (8, "maintenance.png"),
];

foreach (var (tab, file) in shots)
{
    vm.SelectedTabIndex = tab;
    Pump();
    var frame = window.CaptureRenderedFrame();
    var path = Path.Combine(outDir, file);
    frame?.Save(path);
    Console.WriteLine($"saved {file} ({frame?.PixelSize})");
}

Console.WriteLine("done");
return;

static void Pump()
{
    for (var i = 0; i < 25; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(15);
    }
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
}

static SlopworksHost BuildHost(string root)
{
    var paths = new SlopworksPaths(root);
    paths.EnsureCreated();

    var config = new SlopworksConfig();
    var http = new HttpClient();
    var logger = new FileLoggerProvider(paths.LogsDir).CreateLogger("shots");
    var journal = FileStateJournal.Load(paths.JournalFile);
    var linux = new WslLinuxCommandFactory(SlopworksPaths.DistroName);

    var profiles = new ProfileManager(paths, config);
    var models = new ModelLibrary(paths, config, profiles);
    var platforms = new PlatformManager(paths, config, profiles);

    var host = new SlopworksHost
    {
        Paths = paths,
        Config = config,
        Profiles = profiles,
        Models = models,
        Platforms = platforms,
        ModelInspector = new ModelInspector(http, config),
        Logger = logger,
        Journal = journal,
        SystemInfo = new FakeSystemInfo(),
        ProcessRunner = new FakeProcessRunner(),
        CommandLog = new FileCommandLog(paths.LogsDir),
        Resolver = new ArtifactResolver(config, journal, http, logger),
        Downloader = new Downloader(http),
        ShellIntegration = new FakeShell(),
        Linux = linux,
        Server = new VllmServerController(linux, config, http, paths),
        NetworkExposure = new FakeNetwork(),
        Metrics = new FakeMetrics(),
        GpuMetrics = new FakeGpuMetrics(),
        Wsl = new FakeWsl(),
    };

    Seed(host);
    return host;
}

static void Seed(SlopworksHost host)
{
    // Extra platforms + default.
    host.Platforms.Create("CUDA 12.8 · stable");
    host.Platforms.Create("CUDA nightly");

    // Extra profiles; land on a nicely-named active one and give it rich settings.
    host.Profiles.Create("RTX 5090 · fp8");
    host.Profiles.Create("2x RTX 3090 · AWQ");
    host.Profiles.Switch("RTX 5090 · fp8");

    var s = host.Config.Server;
    s.Model = "Qwen/Qwen2.5-32B-Instruct-AWQ";
    s.Quantization = "auto";
    s.MaxModelLen = 32768;
    s.KvCacheDtype = "fp8";
    s.TensorParallelSize = 2;
    s.EnableToolCalling = true;
    host.Profiles.SaveActive();

    // Model library with cached metadata + notes + advanced on.
    host.Models.ShowAdvanced = true;
    AddModel(host, "Qwen/Qwen2.5-32B-Instruct-AWQ", "Servable", "Servable — awq safetensors",
        "Safetensors checkpoint, quantization method 'awq'. vLLM supports this method.",
        "awq", "Qwen2ForCausalLM", 32_763_876_352, 32768, "bfloat16", "text-generation", "apache-2.0", 812_000,
        "Sweet spot for 2x3090 with tensor-parallel 2.");
    AddModel(host, "Qwen/Qwen2.5-7B-Instruct", "Servable", "Servable — full-precision safetensors",
        "Full-precision safetensors (no quantization_config).",
        "none", "Qwen2ForCausalLM", 7_615_616_512, 32768, "bfloat16", "text-generation", "apache-2.0", 2_100_000,
        "Fits comfortably on a single 24 GB card.");
    AddModel(host, "TheBloke/Llama-2-13B-GGUF", "Unservable", "GGUF-only — vLLM can't serve this",
        "The repo ships .gguf files (llama.cpp/Ollama format) and no safetensors.",
        "gguf", null, null, null, null, "text-generation", "llama2", 480_000,
        "GGUF — run this one in Ollama, not vLLM.");
    host.Models.Save();

    // A chat template, attached to the current server model.
    new TemplateStore(host.Paths).Create("custom-chatml",
        "{%- for message in messages -%}\n  {{- '<|im_start|>' + message.role + '\\n' + message.content + '<|im_end|>\\n' -}}\n{%- endfor -%}\n{{- '<|im_start|>assistant\\n' -}}\n");
    host.Models.SetActiveModelTemplate("custom-chatml");
}

static void AddModel(SlopworksHost host, string id, string verdict, string summary, string detail,
    string? quant, string? arch, long? paramCount, int? maxCtx, string? dtype, string? pipeline,
    string? license, long? downloads, string notes)
{
    var entry = host.Models.Add(id);
    entry.Notes = notes;
    entry.Verdict = verdict;
    entry.Summary = summary;
    entry.Detail = detail;
    entry.Quant = quant;
    entry.Architecture = arch;
    entry.Parameters = paramCount;
    entry.MaxContext = maxCtx;
    entry.Dtype = dtype;
    entry.Pipeline = pipeline;
    entry.License = license;
    entry.Downloads = downloads;
    entry.CheckedAt = "2026-07-23 15:40";
}
