using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using MoonSharp.VsCodeDebugger;

namespace MoonSharp.Interpreter
{
	public static class TtsDebugger
	{
		private const int DETACH_OLD_TIMEOUT = 100;

		private static readonly Dictionary<string, Script> namedScripts = new Dictionary<string, Script>();
		private static readonly Dictionary<string, Script> oldNamedScripts = new Dictionary<string, Script>();

		private static readonly Dictionary<Script, string> scriptNames = new Dictionary<Script, string>();
		private static readonly Dictionary<Script, string> oldScriptNames = new Dictionary<Script, string>();

		private static readonly Random random = new Random();

		private static readonly object detachOldLock = new object();

		private static MoonSharpVsCodeDebugServer server;
		private static Timer detachTimer = null;

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

		private static string RandomScriptName()
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

		public static string OnDoString(Script script)
		{
			lock (detachOldLock)
			{
				CancelDetachOldTimer();

				string scriptName = (scriptNames ?? oldScriptNames)?.GetOrDefault(script) ?? GetReflectedScriptName(script) ?? RandomScriptName();
				Script currentScript = namedScripts.GetOrDefault(scriptName);

				if (script != currentScript)
				{
					Script oldScript;

					if (currentScript != null)
					{
						// When we encounter a new Script for a known script name (typically 'Global'), we assume TTS has loaded (or reloaded) a mod.
						DetachOldScripts();

						foreach (KeyValuePair<string, Script> namedScript in namedScripts)
						{
							oldNamedScripts.Add(namedScript.Key, namedScript.Value);
							oldScriptNames.Add(namedScript.Value, namedScript.Key);
						}

						namedScripts.Clear();
						scriptNames.Clear();

						oldScript = currentScript;
					}
					else
					{
						oldScript = oldNamedScripts.GetOrDefault(scriptName);
					}

					namedScripts.Add(scriptName, script);
					scriptNames.Add(script, scriptName);

					if (oldScript != null)
					{
						oldScriptNames.Remove(oldScript);
						oldNamedScripts.Remove(scriptName);

						GetServer().ReplaceAttachedScript(oldScript, script, scriptName);
					}
					else
					{
						GetServer().AttachToScript(script, scriptName);
					}
				}

				return scriptName;
			}
		}

		public static void OnStringDone(Script script)
		{
			SetDetachOldTimer();
		}
	}
}
