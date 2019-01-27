#if (!PCL) && ((!UNITY_5) || UNITY_STANDALONE)

using System.Collections.Generic;
using MoonSharp.Interpreter;
using MoonSharp.VsCodeDebugger.SDK;

namespace MoonSharp.VsCodeDebugger.DebuggerLogic
{
	internal static class VariableInspector
	{
		internal static void InspectVariable(DynValue v, List<Variable> variables)
		{
			variables.Add(new Variable("(value)", v.ToPrintString(), v.Type.ToLuaDebuggerString()));
			variables.Add(new Variable("(val #id)", v.ReferenceID.ToString(), null));

			switch (v.Type)
			{
				case DataType.Tuple:
					for (int i = 0; i < v.Tuple.Length; i++)
					{
						DynValue value = v.Tuple[i] ?? DynValue.Void;
						variables.Add(new Variable("[i]", value.ToDebugPrintString(), value.Type.ToLuaDebuggerString()));
					}
					break;
				case DataType.Function:
					variables.Add(new Variable("(address)", v.Function.EntryPointByteCodeLocation.ToString("X8"), null));
					variables.Add(new Variable("(upvalues)", v.Function.GetUpvaluesCount().ToString(), null));
					variables.Add(new Variable("(upvalues type)", v.Function.GetUpvaluesType().ToString(), null));
					break;
				case DataType.Table:
					string tableType;

					if (v.Table.MetaTable != null && (v.Table.OwnerScript == null))
						tableType = "prime table with metatable";
					else if (v.Table.MetaTable != null)
						tableType = "table with metatable";
					else if (v.Table.OwnerScript == null)
						tableType = "prime table";
					else
						tableType = "table";

					variables.Add(new Variable("(table #id)", v.Table.ReferenceID.ToString(), tableType));

					if (v.Table.MetaTable != null)
						variables.Add(new Variable("(metatable #id)", v.Table.MetaTable.ReferenceID.ToString(), "table"));

					variables.Add(new Variable("(length)", v.Table.Length.ToString(), DataType.Number.ToLuaDebuggerString()));

					foreach (TablePair p in v.Table.Pairs)
						variables.Add(new Variable("[" + p.Key.ToDebugPrintString() + "]", p.Value.ToDebugPrintString(), p.Value.Type.ToLuaDebuggerString()));

					break;
				case DataType.UserData:
					if (v.UserData.Descriptor != null)
					{
						variables.Add(new Variable("(descriptor)", v.UserData.Descriptor.Name, null));
					}
					else
					{
						variables.Add(new Variable("(descriptor)", "null!", DataType.Nil.ToLuaDebuggerString()));
					}

					variables.Add(new Variable("(native object)", v.UserData.Object != null ? v.UserData.Object.ToString() : "(null)", null));
					break;
				case DataType.Thread:
					variables.Add(new Variable("(coroutine state)", v.Coroutine.State.ToString(), null));
					variables.Add(new Variable("(coroutine type)", v.Coroutine.Type.ToString(), null));
					variables.Add(new Variable("(auto-yield counter)", v.Coroutine.AutoYieldCounter.ToString(), null));
					break;
				case DataType.ClrFunction:
					variables.Add(new Variable("(name)", v.Callback.Name ?? "(unnamed)", null));
					break;
				case DataType.TailCallRequest:
				case DataType.YieldRequest:
				case DataType.Nil:
				case DataType.Void:
				case DataType.Boolean:
				case DataType.Number:
				case DataType.String:
				default:
					break;
			}
		}
	}
}

#endif