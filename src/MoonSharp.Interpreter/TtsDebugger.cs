using System;
using System.Reflection;
using MoonSharp.VsCodeDebugger;

namespace MoonSharp.Interpreter
{
    public static class TtsDebugger
    {
        private static MoonSharpVsCodeDebugServer server;

        public static MoonSharpVsCodeDebugServer GetServer()
        {
            if (server == null)
            {
                server = new MoonSharpVsCodeDebugServer();
                server.Start();
            }

            return server;
        }

        public static string GetScriptName(Type luaScriptType, Script script)
        {
            Type baseLuaScriptType = luaScriptType.BaseType;

            if (baseLuaScriptType != null)
            {
                MethodInfo scriptToLuaScriptMethod = baseLuaScriptType.GetMethod("ScriptToLuaScript");

                if (scriptToLuaScriptMethod != null)
                {
                    object luaScript = scriptToLuaScriptMethod.Invoke(null, new object[] {script});

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
            }

            return null;
        }
    }
}