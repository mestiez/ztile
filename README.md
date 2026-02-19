# ztile

> [!NOTE]  
> This is unfinished and probably won't work properly

A utility for X11 that allows windows to slot into pre-configured zones.

## Usage

Run the program and move windows with your mouse. Drop them over a configured zone to slot them into place.

## Configuration

Create `~/.config/ztile.toml`:

```toml
padding = 10
zones = [
    "1224x2128+1920+0",
    # Define zones using X11 geometry format
]
```

To select zones, you can use a tool like [slop](https://github.com/naelstrof/slop).

## Building

You need .NET 10 to use this program.

You can run it directly with `dotnet run ztile.cs`, or:

```bash
mv ztile.cs ztile # remove extension
chmod +x ztile # make exeutable
./ztile # run!
```

It's also possible to build a native binary by running the `build.sh` script. It will output `ztile` in the `dist` directory.

## Dependencies

- TerraFX.Interop.Xlib
- System.CommandLine
- Tomlyn.Signed

