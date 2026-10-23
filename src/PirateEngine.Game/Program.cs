using PirateEngine.Game;

// Command line:
//   --seed N            world seed (default: random)
//   --size N            map width/height in tiles (default 1024; 256..4096)
//   --load FILE         load a save instead of generating
//   --setup ID          world-setup preset from setup.json (standard, frontier, crowded, charted, empty, ...)
//   --scenario NAME     demo set-up: overview | harbour | goods | battle | voyage
//   --screenshot FILE   save a PNG after --frames frames, then exit (for docs / CI)
//   --frames N          frames to wait before the screenshot (default 90)
//   --window WxH        initial window size (default 1600x900)
var opt = new GameOptions();
for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");
    switch (args[i])
    {
        case "--seed": opt.Seed = int.Parse(Next()); break;
        case "--size": opt.Size = Math.Clamp(int.Parse(Next()), 256, 4096); break;
        case "--load": opt.LoadPath = Next(); break;
        case "--setup": opt.Setup = Next(); break;
        case "--scenario": opt.Scenario = Next(); break;
        case "--screenshot": opt.ScreenshotPath = Next(); break;
        case "--frames": opt.ScreenshotFrames = int.Parse(Next()); break;
        case "--window":
            var parts = Next().Split('x');
            opt.WindowWidth = int.Parse(parts[0]);
            opt.WindowHeight = int.Parse(parts[1]);
            break;
        case "--help":
        case "-h":
            Console.WriteLine("PirateEngine [--seed N] [--size N] [--load FILE] [--setup ID] [--scenario NAME] [--screenshot FILE --frames N] [--window WxH]");
            return;
    }
}

using var game = new PirateGame(opt);
game.Run();
