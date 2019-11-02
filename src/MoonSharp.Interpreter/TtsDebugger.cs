using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.VsCodeDebugger;

namespace MoonSharp.Interpreter
{
	public static class TtsDebugger
	{
		private const int DETACH_OLD_TIMEOUT = 100;

		private static readonly Random random = new Random();

		private static readonly Dictionary<string, Script> namedScripts = new Dictionary<string, Script>();
		private static readonly Dictionary<string, Script> oldNamedScripts = new Dictionary<string, Script>();

		private static readonly Dictionary<Script, string> scriptNames = new Dictionary<Script, string>();
		private static readonly Dictionary<Script, string> oldScriptNames = new Dictionary<Script, string>();

		private static readonly string tempPath = Path.Combine(Path.GetTempPath(), "TTS_DEBUG_" + RandomIdentifier());

		private static readonly object detachOldLock = new object();

		private static MoonSharpVsCodeDebugServer server;
		private static Timer detachTimer = null;

		static TtsDebugger()
		{
			if (!Directory.Exists(tempPath))
			{
				Directory.CreateDirectory(tempPath);
			}
		}

		private static void DetachOldScripts()
		{
			lock (detachOldLock)
			{
				detachTimer?.Dispose();
				detachTimer = null;

				foreach (Script script in oldNamedScripts.Values)
				{
					GetServer().Detach(script);
				}

				oldNamedScripts.Clear();
				oldScriptNames.Clear();
			}
		}

		private static void CancelDetachOldTimer()
		{
			lock (detachOldLock)
			{
				detachTimer?.Dispose();
				detachTimer = null;
			}
		}

		// We don't know exactly when TTS has finished loading all scripts, but we don't want to hang onto old scripts forever. So after loading each script, we
		// give TTS a small period of time to load/replace additional scripts. If no additional script is loaded when the time period is exhausted we assume all
		// scripts that were not replaced are no longer relevant to the presently loaded mod, and we detach them.
		private static void SetDetachOldTimer()
		{
			lock (detachOldLock)
			{
				Timer timer = null;
				timer = new Timer(s => {
					lock (detachOldLock)
					{
						if (detachTimer == timer)
						{
							DetachOldScripts();
						}
						else
						{
							timer?.Dispose();
						}
					}
				}, null, DETACH_OLD_TIMEOUT,  Timeout.Infinite);

				detachTimer?.Dispose();
				detachTimer = timer;
			}
		}

		public static MoonSharpVsCodeDebugServer GetServer()
		{
			if (server == null)
			{
				server = new MoonSharpVsCodeDebugServer();
				server.Start();
			}

			return server;
		}

		private static string RandomIdentifier()
		{
			const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
			return new string(Enumerable.Repeat(chars, 8).Select(s => s[random.Next(s.Length)]).ToArray());
		}

		private static string GetReflectedScriptName(Script script)
		{
			MethodBase callerMethod = new StackTrace().GetFrame(3)?.GetMethod();
			Type luaScriptType = callerMethod?.ReflectedType;
			MethodInfo scriptToLuaScriptMethod = luaScriptType?.BaseType?.GetMethod("ScriptToLuaScript");

			if (scriptToLuaScriptMethod != null)
			{
				object luaScript = scriptToLuaScriptMethod.Invoke(null, new object[] { script });

				if (luaScript != null)
				{
					MethodInfo getScriptNameMethod = luaScript.GetType().GetMethod("GetScriptName");

					if (getScriptNameMethod != null)
					{
						object name = getScriptNameMethod.Invoke(luaScript, new object[] { });

						if (name != null)
						{
							return Convert.ToString(name);
						}
					}
				}
			}

			return null;
		}

		private static string GetCodeFriendlyNameSuffix(string scriptName, Script script)
		{
			if (new StackTrace().GetFrame(3)?.GetMethod().Name == "AddFunctions")
			{
				return scriptName + "__tts_" + script.SourceCodeCount;
			}

			return scriptName;
		}

		public static string OnDoString(Script script)
		{
			if (new StackTrace().GetFrame(2).GetMethod().Name == "ExecuteScript")
			{
				return null; // Script is a code fragment executed from the console, not much sense debugging it.
			}

			lock (detachOldLock)
			{
				CancelDetachOldTimer();

				var scriptName = (scriptNames ?? oldScriptNames)?.GetOrDefault(script) ?? GetReflectedScriptName(script) ?? RandomIdentifier();
				var existingScript = namedScripts.GetOrDefault(scriptName);

				if (script == existingScript)
				{
					return GetCodeFriendlyNameSuffix(scriptName, script);
				}

				Script oldScript;

				if (existingScript != null)
				{
					// When we encounter a new Script for a current script name (typically 'Global'), we assume TTS has loaded (or reloaded) a mod.
					DetachOldScripts();

					foreach (var namedScript in namedScripts)
					{
						oldNamedScripts.Add(namedScript.Key, namedScript.Value);
						oldScriptNames.Add(namedScript.Value, namedScript.Key);
					}

					namedScripts.Clear();
					scriptNames.Clear();

					oldScript = existingScript;
				}
				else
				{
					// A mod may be in the process of loading, so if we didn't find a current script we should check for old scripts.
					oldScript = oldNamedScripts.GetOrDefault(scriptName);
				}

				namedScripts.Add(scriptName, script);
				scriptNames.Add(script, scriptName);

				if (oldScript != null)
				{
					oldScriptNames.Remove(oldScript);
					oldNamedScripts.Remove(scriptName);

					GetServer().ReplaceAttachedScript(oldScript, script, scriptName, SourceCodeToTempPath);
				}
				else
				{
					GetServer().AttachToScript(script, scriptName, SourceCodeToTempPath);
				}

				return GetCodeFriendlyNameSuffix(scriptName, script);
			}
		}

		public static void OnStringDone(Script script)
		{
			SetDetachOldTimer();
		}

		private static string SourceCodeToTempPath(SourceCode sourceCode)
		{
			var scriptName = scriptNames[sourceCode.OwnerScript];
			var sourceName = (scriptName == sourceCode.Name ? scriptName : $"{scriptName}_{sourceCode.Name}").Replace(Path.DirectorySeparatorChar, '_');

			var path = Path.Combine(tempPath, sourceName + ".ttslua");

			if (!File.Exists(path))
			{
				File.Create(path).Close();
			}

			File.WriteAllText(path, sourceCode.Code);

			return path;
		}
	}
}
