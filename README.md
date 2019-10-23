# Tabletop Simulator Enhanced MoonSharp (with Debugging)

This is a fork of [MoonSharp](https://www.moonsharp.org/) the Lua interpreter utilised by Tabletop Simulator (and many other games) that has been modified to improve the Tabletop Simulator mod development experience.

![Demo](https://tts-community.github.io/moonsharp/demo.gif)

# Why?

Tabletop Simulator allows you to script your mods in Lua. There's even an official [TTS plugin for Atom](https://api.tabletopsimulator.com/atom/). *However*, there's no official way to __debug__ your workshop mods - if something goes wrong in your mod's Lua, there's usually a lot of trial and error trying to work out what is going wrong.

However, the latest version of MoonSharp (over 2 years old) actually provides official support for debugging in [VSCode](https://code.visualstudio.com/) the functionality simply hasn't been included with Tabletop Simulator. What we've done here is built a drop-in replacement, for TTS' MoonSharp DLL, which comes with VSCode debugging support pre-enabled.

Additionally, Tabletop Simulator runs workshop mods in sandbox. This is actually a good practice as Berserk Games are protecting users from distributing harmful mods. However, being in a sandbox can make development a bit frustrating (e.g. the lack of the `debug` module). As such, this fork also disables the sandbox _for you_. You still won't be able to develop and distribute mods to end-users that aren't sandboxed; because to disable the sandbox you need this DLL running on your computer.

*__A Note on Security__: It's suggested you only run Tabletop Simulator with this modified MoonSharp interpreter only for development (and perhaps hosting) of your own mods, and _never_ when running someone else's code (e.g. workshop mods). Berserk Games have the sandbox enabled for a reason!*

# How do I use this?

We provide releases (available above), however, if you have Mono installed you can also compile this yourself with:

```
msbuild /t:Restore src/MoonSharp.Interpreter/MoonSharp.Interpreter.csproj
msbuild /p:Configuration=Release src/MoonSharp.Interpreter/MoonSharp.Interpreter.csproj
```
(builds to `src/MoonSharp.Interpreter/bin/Release/net35/MoonSharp.Interpreter.dll`)

Once you've either downloaded or built the DLL, it's simply a matter of copying the DLL over the version distributed with Tabletop Simulator. You may wish to backup the original file, however you can also retrieve the original by, in Steam, right clicking on TTS and chosing "Properties -> Updates > Verify Integrity of Game Files..."; which will cause the original DLL to be downloaded again.

To find your Tabletop Simulator directory, in Steam right click on TTS and chose "Properties -> Local Files -> Browse Local Files...". From here it's platform specific, but you're looking for the directory `Data/Managed`, it is in this directory that you should drop the enhanced interpreter DLL (overwriting the existing file with the same name).

On macOS application contents are hidden, right click on "Tabletop Simulator" and chose "Show Package Contents", then navigate to `Contents/Resources/Data/Managed`.

# How do I know it worked?

If you're running this enhanced MoonSharp interpreter, your Lua environment won't be sandboxed. The simplest way to check this is to paste the following in TTS' chat window (in game):

```
/execute print(debug.traceback())
```

If you see white text printed, you're not sandboxed. If you get an error (red text) then you're still running the original MoonSharp TTS interpreter.

# Debugging

Okay, this is what you're all here for...

Unfortunately, the setup process is a bit clunky.

1. Download and install [VSCode](https://code.visualstudio.com/).

2. Download the [latest release](https://github.com/tts-community/moonsharp/releases) of our enhanced MoonSharp VSCode extension and [install it](https://code.visualstudio.com/docs/editor/extension-gallery#_install-from-a-vsix). If you have previously install the official MoonSharp plugin - please uninstall it first.

2a. You _probably_ also want [EmmyLua](https://marketplace.visualstudio.com/items?itemName=tangzx.emmylua) and to configure the [".ttslua" extension to be treated as "lua"](https://stackoverflow.com/questions/29973619/how-to-make-vs-code-to-treat-other-file-extensions-as-certain-language) files.

3. Launch VSCode and open (File -> Open) the directory where you keep all your Lua code.

  Technically, you can open any directoy you chose, but if you want to do code editing in VSCode then you should have a dedicated directory somewhere.

 __Note__: *Please* don't only store your code in a TTS mod. Your scripts and UI should be nothing more than a single `#include file` or ```<Include src="file"/>```

4. Open *any* Lua file in VSCode.

5. Launch TTS and open your mod.

6. In VSCode swap to the [Debug View](https://code.visualstudio.com/docs/editor/debugging).

7. Press the green debug (Play) arrow.

8. From the drop-down, select "MoonSharp". When prompted for a port, simply hit enter (we're using the default MoonSharp debug server port).

9. You will now be presented with a list of scripts loaded within your mod. Press "OK" to debug them all, otherwise deselect any scripts you don't want to debug, then press "OK".

Because Tabletop Simulator is loading several scripts from one workshop save, we unfortunately don't have file paths available to us (because all the scripts are coming from one file). As such, VSCode won't automatically display your code. _However_, if an error is encountered execution will pause and than your code _will_ be automatically displayed, you'll also see a stack trace on the left and be able to mouse over variables to see their values. Alternatively, you can right-click on a script (Main Thread) and chose "Pause" which will cause your script to break (and its source to be shown) as soon as possible.

Once source for your script is displayed, you may proceed to place breakpoints by clicking to the left of a line number in VSCode. TTS will correctly pause when it reaches these breakpoints.

