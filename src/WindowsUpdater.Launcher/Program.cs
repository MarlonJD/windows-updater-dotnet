using WindowsUpdater;

var installRoot = args.Length > 0
    ? Path.GetFullPath(args[0])
    : AppContext.BaseDirectory;
var result = await new LauncherCore(new CurrentVersionStore(installRoot)).LaunchActiveVersionAsync();

if (!result.Started)
{
    Console.Error.WriteLine(result.Error);
    return 1;
}

return 0;
