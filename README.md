# Tabletop Simulator Enhanced MoonSharp (with Debugging)

This is a fork of [MoonSharp](https://www.moonsharp.org/) the Lua interpreter utilised by Tabletop Simulator (and many other games) that has been modified to improve the Tabletop Simulator mod development experience.

# Why?

Tabletop Simulator allows you to script your mods in Lua. There's even an official [TTS plugin for Atom](https://api.tabletopsimulator.com/atom/). *However*, there's no official way to __debug__ your workshop mods - if something goes wrong in your mod's Lua, there's usually a lot of trial and error trying to work out what is going wrong.

However, the latest version of MoonSharp (over 2 years old) actually provides official support for debugging in [VSCode](https://code.visualstudio.com/) the functionality simply hasn't been included with Tabletop Simulator. What we've done here is built a drop-in replacement, for TTS' MoonSharp DLL, which comes with VSCode debugging support pre-enabled.

Additionally, Tabletop Simulator runs workshop mods in sandbox. This is actually a good practice as Berserk Games are protecting users from distributing harmful mods. However, being in a sandbox can make development a bit frustrating (e.g. the lack of the `debug` module). As such, this fork also disables the sandbox _for you_. You still won't be able to develop and distribute mods to end-users that aren't sandboxed; because to disable the sandbox you need this DLL running on your computer.

*__A Note on Security__: It's suggested you only run Tabletop Simulator with this modified MoonSharp interpreter only for development (and perhaps hosting) of your own mods, and _never_ when running someone else's code (e.g. workshop mods). Berserk Games have the sandbox enabled for a reason!*

# How do I use this?

We provide releases (available above), however, if you have Mono installed you can also compile this yourself with:

```
xbuild /p:TargetFrameworkProfile='' /p:Configuration=Release src/moonsharp_ci_net35.sln
```
(builds to `src/MoonSharp.Interpreter/bin/Release/MoonSharp.Interpreter.dll`)

Once you've either downloaded or built the DLL, it's simply a matter of copying the DLL over the version distributed with Tabletop Simulator. You may wish to backup the original file, however you can also retrieve the original by, in Steam, right clicking on TTS and chosing "Properties -> Updates > Verify Integrity of Game Files..."; which will cause the original DLL to be downloaded again.

To find your Tabletop Simulator directory, in Steam right click on TTS and chose "Properties -> Local Files -> Browse Local Files...". From here it's platform specific, but you're looking for the directory `Data/Managed`, it is in this directory that you should drop the enhanced interpreter DLL (overwriting the existing file with the same name).

On macOS application contents are hidden, right click on "Tabletop Simulator" and chose "Show Package Contents", then navigate to "Contents/Resources/Data/Managed`.

# How do I know it worked?

If you're running this enhanced MoonSharp interpreter, your Lua environment won't be sandboxed. The simplest way to check this is to paste the following in TTS' chat window (in game):

```
/execute print(debug.traceback())
```

If you see white text printed, your not sandboxed. If you get an error (red text) then you're still running the original MoonSharp TTS interpreter.

# Debugging

Okay, this is what you're all here for...

Unfortunately, the setup process is a bit clunky.

1. Download and install [VSCode](https://code.visualstudio.com/).

2. Download and install the official [MoonSharp Debugging plugin](https://marketplace.visualstudio.com/items?itemName=xanathar.moonsharp-debug).

3. Launch VSCode and open (File -> Open) the directory where you keep all your Lua code.

  Technically, you can open any directoy you chose, but if you want to do code editing in VSCode then you should have a dedicated directory somewhere.

 __Note__: *Please* don't only store your code in a TTS mod. Your scripts and UI should be nothing more than a single `#include file` or `<Include src="file"/>

4. In the chosen directory, create a new file called `launch.json` and paste the following:

```json
{
    "version": "0.2.0",
    "configurations": [
        {
            "name": "MoonSharp Attach",
            "debugServer": 41912,
            "type": "moonsharp-debug",
            "request": "attach"
       }
    ]
}
```

5. Launch TTS and open your mod.

6. In VSCode swap to the [Debug View](https://code.visualstudio.com/docs/editor/debugging).

7. (Only need to do this once) Near the top of the window chose "No Configurations > Add Configuration... > MoonSharp Attach".

8. With the "MoonSharp Attach" configuration selected, press the Green Run icon.

The MoonSharp Debug plugin will now take over and automatically connect to Tabletop Simulator and download the scripts.

Unfortunately, at present although you can list all scripts by typing `!list` down the bottom, you can only debug one at a time, see `!help` for more info.

Because Tabletop Simulator is loading several scripts from one workshop save, we unfortunately don't have file paths available to us (because all the scripts are coming from one file). As such, VSCode won't automatically display your code. _However_, if an error is encountered execution will pause and than your code _will_ be automatically displayed, you'll also see a stack trace on the left and be able to mouse over variables to see their values.

Once the code is displayed, you may proceed to place breakpoints by clicking to the left of a line number in VSCode. TTS will correctly pause when it reaches these breakpoints, however placing breakpoints in Lua source files on your file system simply won't work.

