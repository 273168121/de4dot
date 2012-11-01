﻿/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Metadata;

//TODO: Remove these
using DN = dot10.DotNet;
using DNE = dot10.DotNet.Emit;
using DNM = dot10.DotNet.MD;

namespace de4dot.blocks {
	public enum FrameworkType {
		Unknown,
		Desktop,
		Silverlight,		// and WindowsPhone, XNA Xbox360
		CompactFramework,
		XNA,
		Zune,
	}

	class TypeCache {
		ModuleDefinition module;
		de4dot.blocks.OLD_REMOVE.TypeDefinitionDict<TypeDefinition> typeRefToDef = new de4dot.blocks.OLD_REMOVE.TypeDefinitionDict<TypeDefinition>();

		public TypeCache(ModuleDefinition module) {
			this.module = module;
			init();
		}

		void init() {
			foreach (var type in module.GetTypes())
				typeRefToDef.add(type, type);
		}

		public TypeDefinition lookup(TypeReference typeReference) {
			return typeRefToDef.find(typeReference);
		}
	}

	public class TypeCaches {
		Dictionary<ModuleDefinition, TypeCache> typeCaches = new Dictionary<ModuleDefinition, TypeCache>();

		// Should be called when the whole module is reloaded or when a lot of types have been
		// modified (eg. renamed)
		public void invalidate(ModuleDefinition module) {
			if (module == null)
				return;
			typeCaches.Remove(module);
		}

		// Call this to invalidate all modules
		public List<ModuleDefinition> invalidateAll() {
			var list = new List<ModuleDefinition>(typeCaches.Keys);
			typeCaches.Clear();
			return list;
		}

		public TypeDefinition lookup(ModuleDefinition module, TypeReference typeReference) {
			TypeCache typeCache;
			if (!typeCaches.TryGetValue(module, out typeCache))
				typeCaches[module] = typeCache = new TypeCache(module);
			return typeCache.lookup(typeReference);
		}
	}

	public class CallCounter {
		Dictionary<de4dot.blocks.OLD_REMOVE.MethodReferenceAndDeclaringTypeKey, int> calls = new Dictionary<de4dot.blocks.OLD_REMOVE.MethodReferenceAndDeclaringTypeKey, int>();

		public void add(MethodReference calledMethod) {
			int count;
			var key = new de4dot.blocks.OLD_REMOVE.MethodReferenceAndDeclaringTypeKey(calledMethod);
			calls.TryGetValue(key, out count);
			calls[key] = count + 1;
		}

		public MethodReference most() {
			int numCalls;
			return most(out numCalls);
		}

		public MethodReference most(out int numCalls) {
			MethodReference method = null;
			int callCount = 0;
			foreach (var key in calls.Keys) {
				if (calls[key] > callCount) {
					callCount = calls[key];
					method = key.MethodReference;
				}
			}
			numCalls = callCount;
			return method;
		}
	}

	public class MethodCalls {
		Dictionary<string, int> methodCalls = new Dictionary<string, int>(StringComparer.Ordinal);

		public void addMethodCalls(MethodDefinition method) {
			if (!method.HasBody)
				return;
			foreach (var instr in method.Body.Instructions) {
				var calledMethod = instr.Operand as MethodReference;
				if (calledMethod != null)
					add(calledMethod);
			}
		}

		public void add(MethodReference method) {
			string key = method.FullName;
			if (!methodCalls.ContainsKey(key))
				methodCalls[key] = 0;
			methodCalls[key]++;
		}

		public int count(string methodFullName) {
			int count;
			methodCalls.TryGetValue(methodFullName, out count);
			return count;
		}

		public bool called(string methodFullName) {
			return count(methodFullName) != 0;
		}
	}

	public static class DotNetUtils {
		public static readonly TypeCaches typeCaches = new TypeCaches();

		public static DN.TypeDef getModuleType(DN.ModuleDef module) {
			return module.GlobalType;
		}

		public static DN.MethodDef getModuleTypeCctor(DN.ModuleDef module) {
			return module.GlobalType.FindClassConstructor();
		}

		public static bool isEmpty(DN.MethodDef method) {
			if (method.CilBody == null)
				return false;
			foreach (var instr in method.CilBody.Instructions) {
				var code = instr.OpCode.Code;
				if (code != DNE.Code.Nop && code != DNE.Code.Ret)
					return false;
			}
			return true;
		}

		public static bool isEmptyObfuscated(DN.MethodDef method) {
			if (method.CilBody == null)
				return false;
			int index = 0;
			var instr = getInstruction(method.CilBody.Instructions, ref index);
			if (instr == null || instr.OpCode.Code != DNE.Code.Ret)
				return false;

			return true;
		}

		public static FieldDefinition findFieldType(TypeDefinition typeDefinition, string typeName, bool isStatic) {
			if (typeDefinition == null)
				return null;
			foreach (var field in typeDefinition.Fields) {
				if (field.FieldType.FullName == typeName && field.IsStatic == isStatic)
					return field;
			}
			return null;
		}

		public static IEnumerable<MethodDefinition> findMethods(IEnumerable<MethodDefinition> methods, string returnType, string[] argsTypes) {
			return findMethods(methods, returnType, argsTypes, true);
		}

		public static IEnumerable<MethodDefinition> findMethods(IEnumerable<MethodDefinition> methods, string returnType, string[] argsTypes, bool isStatic) {
			foreach (var method in methods) {
				if (!method.HasBody || method.CallingConvention != MethodCallingConvention.Default)
					continue;
				if (method.IsStatic != isStatic || method.Parameters.Count != argsTypes.Length)
					continue;
				if (method.GenericParameters.Count > 0)
					continue;
				if (method.MethodReturnType.ReturnType.FullName != returnType)
					continue;
				for (int i = 0; i < argsTypes.Length; i++) {
					if (method.Parameters[i].ParameterType.FullName != argsTypes[i])
						goto next;
				}
				yield return method;
			next: ;
			}
		}

		public static bool isDelegate(DN.IType type) {
			if (type == null)
				return false;
			var fn = type.FullName;
			return fn == "System.Delegate" || fn == "System.MulticastDelegate";
		}

		public static bool derivesFromDelegate(DN.TypeDef type) {
			return type != null && isDelegate(type.BaseType);
		}

		public static bool isSameAssembly(TypeReference type, string assembly) {
			return MemberReferenceHelper.getCanonicalizedScopeName(type.Scope) == assembly.ToLowerInvariant();
		}

		public static bool isMethod(MethodReference method, string returnType, string parameters) {
			return method != null && method.FullName == returnType + " " + method.DeclaringType.FullName + "::" + method.Name + parameters;
		}

		public static bool hasPinvokeMethod(TypeDefinition type, string methodName) {
			return getPInvokeMethod(type, methodName) != null;
		}

		public static MethodDefinition getPInvokeMethod(TypeDefinition type, string methodName) {
			if (type == null)
				return null;
			foreach (var method in type.Methods) {
				if (method.PInvokeInfo == null)
					continue;
				if (method.PInvokeInfo.EntryPoint == methodName)
					return method;
			}
			return null;
		}

		public static MethodDefinition getPInvokeMethod(TypeDefinition type, string dll, string funcName) {
			foreach (var method in type.Methods) {
				if (isPinvokeMethod(method, dll, funcName))
					return method;
			}
			return null;
		}

		public static bool isPinvokeMethod(MethodDefinition method, string dll, string funcName) {
			if (method == null)
				return false;
			if (method.PInvokeInfo == null || method.PInvokeInfo.EntryPoint != funcName)
				return false;
			return getDllName(dll).Equals(getDllName(method.PInvokeInfo.Module.Name), StringComparison.OrdinalIgnoreCase);
		}

		public static string getDllName(string dll) {
			if (dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				return dll.Substring(0, dll.Length - 4);
			return dll;
		}

		public static MethodDefinition getMethod(TypeDefinition type, string name) {
			if (type == null)
				return null;
			foreach (var method in type.Methods) {
				if (method.Name == name)
					return method;
			}
			return null;
		}

		public static MethodDefinition getMethod(TypeDefinition type, MethodReference methodReference) {
			if (type == null || methodReference == null)
				return null;
			if (methodReference is MethodDefinition)
				return (MethodDefinition)methodReference;
			foreach (var method in type.Methods) {
				if (MemberReferenceHelper.compareMethodReference(method, methodReference))
					return method;
			}
			return null;
		}

		public static MethodDefinition getMethod(ModuleDefinition module, MethodReference method) {
			if (method == null)
				return null;
			return getMethod(module, method, method.DeclaringType);
		}

		public static MethodDefinition getMethod2(ModuleDefinition module, MethodReference method) {
			if (method == null)
				return null;
			return getMethod(module, method, method.DeclaringType.GetElementType());
		}

		static MethodDefinition getMethod(ModuleDefinition module, MethodReference method, TypeReference declaringType) {
			if (method == null)
				return null;
			if (method is MethodDefinition)
				return (MethodDefinition)method;
			return getMethod(getType(module, declaringType), method);
		}

		public static MethodDefinition getMethod(TypeDefinition type, string returnType, string parameters) {
			foreach (var method in type.Methods) {
				if (isMethod(method, returnType, parameters))
					return method;
			}
			return null;
		}

		public static DN.MethodDef getMethod2(DN.ModuleDef module, DN.IMethod method) {
			if (method == null)
				return null;
			return getMethod(module, method, method.DeclaringType.ScopeType);
		}

		static DN.TypeDef getType(DN.ModuleDef module, DN.ITypeDefOrRef type) {
			var td = type as DN.TypeDef;
			if (td != null)
				return td;

			var tr = type as DN.TypeRef;
			if (tr != null)
				return tr.Resolve();

			return null;
		}

		static DN.MethodDef getMethod(DN.ModuleDef module, DN.IMethod method, DN.ITypeDefOrRef declaringType) {
			if (method == null)
				return null;
			if (method is DN.MethodDef)
				return (DN.MethodDef)method;
			return getMethod(getType(module, declaringType), method);
		}

		public static DN.MethodDef getMethod(DN.TypeDef type, DN.IMethod methodRef) {
			if (type == null || methodRef == null)
				return null;
			if (methodRef is DN.MethodDef)
				return (DN.MethodDef)methodRef;
			return type.FindMethod(methodRef.Name, methodRef.MethodSig);
		}

		public static IEnumerable<MethodDefinition> getNormalMethods(TypeDefinition type) {
			foreach (var method in type.Methods) {
				if (method.HasPInvokeInfo)
					continue;
				if (method.Name == ".ctor" || method.Name == ".cctor")
					continue;

				yield return method;
			}
		}

		public static TypeDefinition getType(ModuleDefinition module, TypeReference typeReference) {
			if (typeReference == null)
				return null;
			if (typeReference is TypeDefinition)
				return (TypeDefinition)typeReference;
			return typeCaches.lookup(module, typeReference);
		}

		public static FieldDefinition getField(ModuleDefinition module, FieldReference field) {
			if (field == null)
				return null;
			if (field is FieldDefinition)
				return (FieldDefinition)field;
			return getField(getType(module, field.DeclaringType), field);
		}

		public static FieldDefinition getField(TypeDefinition type, FieldReference fieldReference) {
			if (type == null || fieldReference == null)
				return null;
			if (fieldReference is FieldDefinition)
				return (FieldDefinition)fieldReference;
			foreach (var field in type.Fields) {
				if (MemberReferenceHelper.compareFieldReference(field, fieldReference))
					return field;
			}
			return null;
		}

		public static FieldDefinition getField(TypeDefinition type, string typeFullName) {
			if (type == null)
				return null;
			foreach (var field in type.Fields) {
				if (field.FieldType.FullName == typeFullName)
					return field;
			}
			return null;
		}

		public static FieldDefinition getFieldByName(TypeDefinition type, string name) {
			if (type == null)
				return null;
			foreach (var field in type.Fields) {
				if (field.Name == name)
					return field;
			}
			return null;
		}

		public static IEnumerable<MethodReference> getMethodCalls(MethodDefinition method) {
			var list = new List<MethodReference>();
			if (method.HasBody) {
				foreach (var instr in method.Body.Instructions) {
					var calledMethod = instr.Operand as MethodReference;
					if (calledMethod != null)
						list.Add(calledMethod);
				}
			}
			return list;
		}

		public static MethodCalls getMethodCallCounts(MethodDefinition method) {
			var methodCalls = new MethodCalls();
			methodCalls.addMethodCalls(method);
			return methodCalls;
		}

		public static bool hasString(MethodDefinition method, string s) {
			if (method == null || method.Body == null)
				return false;
			foreach (var instr in method.Body.Instructions) {
				if (instr.OpCode.Code == Code.Ldstr && (string)instr.Operand == s)
					return true;
			}
			return false;
		}

		public static IList<string> getCodeStrings(MethodDefinition method) {
			var strings = new List<string>();
			if (method != null && method.Body != null) {
				foreach (var instr in method.Body.Instructions) {
					if (instr.OpCode.Code == Code.Ldstr)
						strings.Add((string)instr.Operand);
				}
			}
			return strings;
		}

		public static DN.Resource getResource(DN.ModuleDef module, string name) {
			return getResource(module, new List<string> { name });
		}

		public static DN.Resource getResource(DN.ModuleDef module, IEnumerable<string> strings) {
			if (!module.HasResources)
				return null;

			var resources = module.Resources;
			foreach (var tmp in strings) {
				var resourceName = removeFromNullChar(tmp);
				if (resourceName == null)
					continue;
				var name = new DNM.UTF8String(resourceName);
				foreach (var resource in resources) {
					if (DNM.UTF8String.Equals(resource.Name, name))
						return resource;
				}
			}

			return null;
		}

		static string removeFromNullChar(string s) {
			int index = s.IndexOf((char)0);
			if (index < 0)
				return s;
			return s.Substring(0, index);
		}

		// Copies most things but not everything
		public static MethodDefinition clone(MethodDefinition method) {
			var newMethod = new MethodDefinition(method.Name, method.Attributes, method.MethodReturnType.ReturnType);
			newMethod.MetadataToken = method.MetadataToken;
			newMethod.Attributes = method.Attributes;
			newMethod.ImplAttributes = method.ImplAttributes;
			newMethod.HasThis = method.HasThis;
			newMethod.ExplicitThis = method.ExplicitThis;
			newMethod.CallingConvention = method.CallingConvention;
			newMethod.SemanticsAttributes = method.SemanticsAttributes;
			newMethod.DeclaringType = method.DeclaringType;
			foreach (var arg in method.Parameters)
				newMethod.Parameters.Add(new ParameterDefinition(arg.Name, arg.Attributes, arg.ParameterType));
			foreach (var gp in method.GenericParameters)
				newMethod.GenericParameters.Add(new GenericParameter(gp.Name, newMethod) { Attributes = gp.Attributes });
			copyBodyFromTo(method, newMethod);
			return newMethod;
		}

		// Copies most things but not everything
		public static DN.MethodDef clone(DN.MethodDef method) {
			return null;	//TODO:
		}

		public static Instruction clone(Instruction instr) {
			return new Instruction {
				Offset = instr.Offset,
				OpCode = instr.OpCode,
				Operand = instr.Operand,
				SequencePoint = instr.SequencePoint,
			};
		}

		public static void copyBody(MethodDefinition method, out IList<Instruction> instructions, out IList<ExceptionHandler> exceptionHandlers) {
			if (method == null || !method.HasBody) {
				instructions = new List<Instruction>();
				exceptionHandlers = new List<ExceptionHandler>();
				return;
			}

			var oldInstrs = method.Body.Instructions;
			var oldExHandlers = method.Body.ExceptionHandlers;
			instructions = new List<Instruction>(oldInstrs.Count);
			exceptionHandlers = new List<ExceptionHandler>(oldExHandlers.Count);
			var oldToIndex = Utils.createObjectToIndexDictionary(oldInstrs);

			foreach (var oldInstr in oldInstrs)
				instructions.Add(clone(oldInstr));

			foreach (var newInstr in instructions) {
				var operand = newInstr.Operand;
				if (operand is Instruction)
					newInstr.Operand = instructions[oldToIndex[(Instruction)operand]];
				else if (operand is Instruction[]) {
					var oldArray = (Instruction[])operand;
					var newArray = new Instruction[oldArray.Length];
					for (int i = 0; i < oldArray.Length; i++)
						newArray[i] = instructions[oldToIndex[oldArray[i]]];
					newInstr.Operand = newArray;
				}
			}

			foreach (var oldEx in oldExHandlers) {
				var newEx = new ExceptionHandler(oldEx.HandlerType) {
					TryStart	= getInstruction(instructions, oldToIndex, oldEx.TryStart),
					TryEnd		= getInstruction(instructions, oldToIndex, oldEx.TryEnd),
					FilterStart	= getInstruction(instructions, oldToIndex, oldEx.FilterStart),
					HandlerStart= getInstruction(instructions, oldToIndex, oldEx.HandlerStart),
					HandlerEnd	= getInstruction(instructions, oldToIndex, oldEx.HandlerEnd),
					CatchType	= oldEx.CatchType,
				};
				exceptionHandlers.Add(newEx);
			}
		}

		static Instruction getInstruction(IList<Instruction> instructions, IDictionary<Instruction, int> instructionToIndex, Instruction instruction) {
			if (instruction == null)
				return null;
			return instructions[instructionToIndex[instruction]];
		}

		public static void restoreBody(MethodDefinition method, IEnumerable<Instruction> instructions, IEnumerable<ExceptionHandler> exceptionHandlers) {
			if (method == null || !method.HasBody)
				return;

			var bodyInstrs = method.Body.Instructions;
			bodyInstrs.Clear();
			foreach (var instr in instructions)
				bodyInstrs.Add(instr);

			var bodyExceptionHandlers = method.Body.ExceptionHandlers;
			bodyExceptionHandlers.Clear();
			foreach (var eh in exceptionHandlers)
				bodyExceptionHandlers.Add(eh);
		}


		public static void restoreBody(DN.MethodDef method, IEnumerable<DNE.Instruction> instructions, IEnumerable<DNE.ExceptionHandler> exceptionHandlers) {
			if (method == null || method.CilBody == null)
				return;

			var bodyInstrs = method.CilBody.Instructions;
			bodyInstrs.Clear();
			foreach (var instr in instructions)
				bodyInstrs.Add(instr);

			var bodyExceptionHandlers = method.CilBody.ExceptionHandlers;
			bodyExceptionHandlers.Clear();
			foreach (var eh in exceptionHandlers)
				bodyExceptionHandlers.Add(eh);
		}

		public static void copyBodyFromTo(MethodDefinition fromMethod, MethodDefinition toMethod) {
			if (fromMethod == toMethod)
				return;

			IList<Instruction> instructions;
			IList<ExceptionHandler> exceptionHandlers;
			copyBody(fromMethod, out instructions, out exceptionHandlers);
			restoreBody(toMethod, instructions, exceptionHandlers);
			copyLocalsFromTo(fromMethod, toMethod);
			updateInstructionOperands(fromMethod, toMethod);
		}

		static void copyLocalsFromTo(MethodDefinition fromMethod, MethodDefinition toMethod) {
			var fromBody = fromMethod.Body;
			var toBody = toMethod.Body;

			toBody.Variables.Clear();
			foreach (var local in fromBody.Variables)
				toBody.Variables.Add(new VariableDefinition(local.Name, local.VariableType));
		}

		static void updateInstructionOperands(MethodDefinition fromMethod, MethodDefinition toMethod) {
			var fromBody = fromMethod.Body;
			var toBody = toMethod.Body;

			toBody.InitLocals = fromBody.InitLocals;
			toBody.MaxStackSize = fromBody.MaxStackSize;

			var newOperands = new Dictionary<object, object>();
			var fromParams = getParameters(fromMethod);
			var toParams = getParameters(toMethod);
			if (fromBody.ThisParameter != null)
				newOperands[fromBody.ThisParameter] = toBody.ThisParameter;
			for (int i = 0; i < fromParams.Count; i++)
				newOperands[fromParams[i]] = toParams[i];
			for (int i = 0; i < fromBody.Variables.Count; i++)
				newOperands[fromBody.Variables[i]] = toBody.Variables[i];

			foreach (var instr in toBody.Instructions) {
				if (instr.Operand == null)
					continue;
				object newOperand;
				if (newOperands.TryGetValue(instr.Operand, out newOperand))
					instr.Operand = newOperand;
			}
		}

		public static IEnumerable<CustomAttribute> findAttributes(ICustomAttributeProvider custAttrProvider, TypeReference attr) {
			var list = new List<CustomAttribute>();
			if (custAttrProvider == null)
				return list;
			foreach (var cattr in custAttrProvider.CustomAttributes) {
				if (MemberReferenceHelper.compareTypes(attr, cattr.AttributeType))
					list.Add(cattr);
			}
			return list;
		}

		public static string getCustomArgAsString(CustomAttribute cattr, int arg) {
			if (cattr == null || arg >= cattr.ConstructorArguments.Count)
				return null;
			var carg = cattr.ConstructorArguments[arg];
			if (carg.Type.FullName != "System.String")
				return null;
			return (string)carg.Value;
		}

		public static IEnumerable<MethodDefinition> getCalledMethods(ModuleDefinition module, MethodDefinition method) {
			if (method != null && method.HasBody) {
				foreach (var call in method.Body.Instructions) {
					if (call.OpCode.Code != Code.Call && call.OpCode.Code != Code.Callvirt)
						continue;
					var methodRef = call.Operand as MethodReference;
					if (methodRef == null)
						continue;
					var type = getType(module, methodRef.DeclaringType);
					var methodDef = getMethod(type, methodRef);
					if (methodDef != null)
						yield return methodDef;
				}
			}
		}

		public static IList<Instruction> getInstructions(IList<Instruction> instructions, int i, params OpCode[] opcodes) {
			if (i + opcodes.Length > instructions.Count)
				return null;
			if (opcodes.Length == 0)
				return new List<Instruction>();
			if (opcodes[0] != instructions[i].OpCode)
				return null;

			var list = new List<Instruction>(opcodes.Length);
			for (int j = 0; j < opcodes.Length; j++) {
				var instr = instructions[i + j];
				if (instr.OpCode != opcodes[j])
					return null;
				list.Add(instr);
			}
			return list;
		}

		public static bool hasReturnValue(IMethodSignature method) {
			var type = method.MethodReturnType.ReturnType;
			while (type.IsOptionalModifier || type.IsRequiredModifier)
				type = ((TypeSpecification)type).ElementType;
			return type.EType != ElementType.Void;
		}

		public static void updateStack(Instruction instr, ref int stack, bool methodHasReturnValue) {
			int pushes, pops;
			calculateStackUsage(instr, methodHasReturnValue, out pushes, out pops);
			if (pops == -1)
				stack = 0;
			else
				stack += pushes - pops;
		}

		// Sets pops to -1 if the stack is supposed to be cleared
		public static void calculateStackUsage(Instruction instr, bool methodHasReturnValue, out int pushes, out int pops) {
			if (instr.OpCode.FlowControl == FlowControl.Call)
				calculateStackUsage_call(instr, out pushes, out pops);
			else
				calculateStackUsage_nonCall(instr, methodHasReturnValue, out pushes, out pops);
		}

		static void calculateStackUsage_call(Instruction instr, out int pushes, out int pops) {
			pushes = 0;
			pops = 0;

			var method = (IMethodSignature)instr.Operand;
			bool implicitThis = method.HasThis && !method.ExplicitThis;
			if (hasReturnValue(method) || (instr.OpCode.Code == Code.Newobj && method.HasThis))
				pushes++;

			if (method.HasParameters)
				pops += method.Parameters.Count;
			if (implicitThis && instr.OpCode.Code != Code.Newobj)
				pops++;
			if (instr.OpCode.Code == Code.Calli)
				pops++;
		}

		// Sets pops to -1 if the stack is supposed to be cleared
		static void calculateStackUsage_nonCall(Instruction instr, bool methodHasReturnValue, out int pushes, out int pops) {
			StackBehaviour stackBehavior;

			pushes = 0;
			pops = 0;

			stackBehavior = instr.OpCode.StackBehaviourPush;
			switch (stackBehavior) {
			case StackBehaviour.Push0:
				break;

			case StackBehaviour.Push1:
			case StackBehaviour.Pushi:
			case StackBehaviour.Pushi8:
			case StackBehaviour.Pushr4:
			case StackBehaviour.Pushr8:
			case StackBehaviour.Pushref:
				pushes++;
				break;

			case StackBehaviour.Push1_push1:
				pushes += 2;
				break;

			case StackBehaviour.Varpush:	// only call, calli, callvirt which are handled elsewhere
			default:
				throw new ApplicationException(string.Format("Unknown push StackBehavior {0}", stackBehavior));
			}

			stackBehavior = instr.OpCode.StackBehaviourPop;
			switch (stackBehavior) {
			case StackBehaviour.Pop0:
				break;

			case StackBehaviour.Pop1:
			case StackBehaviour.Popi:
			case StackBehaviour.Popref:
				pops++;
				break;

			case StackBehaviour.Pop1_pop1:
			case StackBehaviour.Popi_pop1:
			case StackBehaviour.Popi_popi:
			case StackBehaviour.Popi_popi8:
			case StackBehaviour.Popi_popr4:
			case StackBehaviour.Popi_popr8:
			case StackBehaviour.Popref_pop1:
			case StackBehaviour.Popref_popi:
				pops += 2;
				break;

			case StackBehaviour.Popi_popi_popi:
			case StackBehaviour.Popref_popi_popi:
			case StackBehaviour.Popref_popi_popi8:
			case StackBehaviour.Popref_popi_popr4:
			case StackBehaviour.Popref_popi_popr8:
			case StackBehaviour.Popref_popi_popref:
				pops += 3;
				break;

			case StackBehaviour.PopAll:
				pops = -1;
				break;

			case StackBehaviour.Varpop:	// call, calli, callvirt, newobj (all handled elsewhere), and ret
				if (methodHasReturnValue)
					pops++;
				break;

			default:
				throw new ApplicationException(string.Format("Unknown pop StackBehavior {0}", stackBehavior));
			}
		}

		public static AssemblyNameReference getAssemblyNameReference(TypeReference type) {
			var scope = type.Scope;
			if (scope == null)
				return null;

			if (scope is ModuleDefinition) {
				var moduleDefinition = (ModuleDefinition)scope;
				return moduleDefinition.Assembly.Name;
			}

			if (scope is AssemblyNameReference)
				return (AssemblyNameReference)scope;

			if (scope is ModuleReference && type.Module.Assembly != null) {
				foreach (var module in type.Module.Assembly.Modules) {
					if (scope.Name == module.Name)
						return type.Module.Assembly.Name;
				}
			}

			throw new ApplicationException(string.Format("Unknown IMetadataScope type: {0}", scope.GetType()));
		}

		public static string getFullAssemblyName(TypeReference type) {
			var asmRef = getAssemblyNameReference(type);
			return asmRef == null ? null : asmRef.FullName;
		}

		public static bool isAssembly(IMetadataScope scope, string assemblySimpleName) {
			return scope.Name == assemblySimpleName ||
				scope.Name.StartsWith(assemblySimpleName + ",", StringComparison.Ordinal);
		}

		public static bool isReferenceToModule(ModuleReference moduleReference, IMetadataScope scope) {
			switch (scope.MetadataScopeType) {
			case MetadataScopeType.AssemblyNameReference:
				var asmRef = (AssemblyNameReference)scope;
				var module = moduleReference as ModuleDefinition;
				return module != null && module.Assembly != null && module.Assembly.Name.FullName == asmRef.FullName;

			case MetadataScopeType.ModuleDefinition:
				return moduleReference == scope;

			case MetadataScopeType.ModuleReference:
				return moduleReference.Name == ((ModuleReference)scope).Name;

			default:
				throw new ApplicationException("Unknown MetadataScopeType");
			}
		}

		public static int getArgIndex(Instruction instr) {
			switch (instr.OpCode.Code) {
			case Code.Ldarg_0: return 0;
			case Code.Ldarg_1: return 1;
			case Code.Ldarg_2: return 2;
			case Code.Ldarg_3: return 3;

			case Code.Ldarga:
			case Code.Ldarga_S:
			case Code.Ldarg:
			case Code.Ldarg_S:
				return getArgIndex(instr.Operand as ParameterDefinition);
			}

			return -1;
		}

		public static int getArgIndex(ParameterDefinition arg) {
			if (arg == null)
				return -1;
			return arg.Sequence;
		}

		public static List<ParameterDefinition> getParameters(MethodReference method) {
			var args = new List<ParameterDefinition>(method.Parameters.Count + 1);
			if (method.HasImplicitThis) {
				var methodDef = method as MethodDefinition;
				if (methodDef != null && methodDef.Body != null)
					args.Add(methodDef.Body.ThisParameter);
				else
					args.Add(new ParameterDefinition(method.DeclaringType, method));
			}
			foreach (var arg in method.Parameters)
				args.Add(arg);
			return args;
		}

		public static ParameterDefinition getParameter(MethodReference method, Instruction instr) {
			return getParameter(getParameters(method), instr);
		}

		public static ParameterDefinition getParameter(IList<ParameterDefinition> parameters, Instruction instr) {
			return getParameter(parameters, getArgIndex(instr));
		}

		public static ParameterDefinition getParameter(IList<ParameterDefinition> parameters, int index) {
			if (0 <= index && index < parameters.Count)
				return parameters[index];
			return null;
		}

		public static List<TypeReference> getArgs(MethodReference method) {
			var args = new List<TypeReference>(method.Parameters.Count + 1);
			if (method.HasImplicitThis)
				args.Add(method.DeclaringType);
			foreach (var arg in method.Parameters)
				args.Add(arg.ParameterType);
			return args;
		}

		public static List<DN.IType> getArgs(DN.IMethod method) {
			var sig = method.MethodSig;
			var args = new List<DN.IType>(sig.Params.Count + 1);
			if (sig.HasThis && !sig.ExplicitThis)
				args.Add(method.DeclaringType);
			foreach (var arg in sig.Params)
				args.Add(arg);
			return args;
		}

		public static TypeReference getArgType(MethodReference method, Instruction instr) {
			return getArgType(getArgs(method), instr);
		}

		public static TypeReference getArgType(IList<TypeReference> methodArgs, Instruction instr) {
			return getArgType(methodArgs, getArgIndex(instr));
		}

		public static TypeReference getArgType(IList<TypeReference> methodArgs, int index) {
			if (0 <= index && index < methodArgs.Count)
				return methodArgs[index];
			return null;
		}

		public static int getArgsCount(MethodReference method) {
			int count = method.Parameters.Count;
			if (method.HasImplicitThis)
				count++;
			return count;
		}

		public static int getArgsCount(DN.IMethod method) {
			var sig = method.MethodSig;
			if (sig == null)
				return 0;
			int count = sig.Params.Count;
			if (sig.HasThis && !sig.ExplicitThis)
				count++;
			return count;
		}

		// Doesn't fix everything (eg. T[] aren't replaced with eg. int[], but T -> int will be fixed)
		public static IList<TypeReference> replaceGenericParameters(GenericInstanceType typeOwner, GenericInstanceMethod methodOwner, IList<TypeReference> types) {
			//TODO: You should use MemberRefInstance.cs
			for (int i = 0; i < types.Count; i++)
				types[i] = getGenericArgument(typeOwner, methodOwner, types[i]);
			return types;
		}

		public static TypeReference getGenericArgument(GenericInstanceType typeOwner, GenericInstanceMethod methodOwner, TypeReference type) {
			var gp = type as GenericParameter;
			if (gp == null)
				return type;

			if (typeOwner != null && MemberReferenceHelper.compareTypes(typeOwner.ElementType, gp.Owner as TypeReference))
				return typeOwner.GenericArguments[gp.Position];

			if (methodOwner != null && MemberReferenceHelper.compareMethodReferenceAndDeclaringType(methodOwner.ElementMethod, gp.Owner as MethodReference))
				return methodOwner.GenericArguments[gp.Position];

			return type;
		}

		public static Instruction getInstruction(IList<Instruction> instructions, ref int index) {
			for (int i = 0; i < 10; i++) {
				if (index < 0 || index >= instructions.Count)
					return null;
				var instr = instructions[index++];
				if (instr.OpCode.Code == Code.Nop)
					continue;
				if (instr.OpCode.OpCodeType == OpCodeType.Prefix)
					continue;
				if (instr == null || (instr.OpCode.Code != Code.Br && instr.OpCode.Code != Code.Br_S))
					return instr;
				instr = instr.Operand as Instruction;
				if (instr == null)
					return null;
				index = instructions.IndexOf(instr);
			}
			return null;
		}

		public static DNE.Instruction getInstruction(IList<DNE.Instruction> instructions, ref int index) {
			for (int i = 0; i < 10; i++) {
				if (index < 0 || index >= instructions.Count)
					return null;
				var instr = instructions[index++];
				if (instr.OpCode.Code == DNE.Code.Nop)
					continue;
				if (instr.OpCode.OpCodeType == DNE.OpCodeType.Prefix)
					continue;
				if (instr == null || (instr.OpCode.Code != DNE.Code.Br && instr.OpCode.Code != DNE.Code.Br_S))
					return instr;
				instr = instr.Operand as DNE.Instruction;
				if (instr == null)
					return null;
				index = instructions.IndexOf(instr);
			}
			return null;
		}

		public static PropertyDefinition createPropertyDefinition(string name, TypeReference propType, MethodDefinition getter, MethodDefinition setter) {
			return new PropertyDefinition(name, PropertyAttributes.None, propType) {
				MetadataToken = nextPropertyToken(),
				GetMethod = getter,
				SetMethod = setter,
			};
		}

		public static EventDefinition createEventDefinition(string name, TypeReference eventType) {
			return new EventDefinition(name, EventAttributes.None, eventType) {
				MetadataToken = nextEventToken(),
			};
		}

		public static FieldDefinition createFieldDefinition(string name, FieldAttributes attributes, TypeReference fieldType) {
			return new FieldDefinition(name, attributes, fieldType) {
				MetadataToken = nextFieldToken(),
			};
		}

		static int nextTokenRid = 0x00FFFFFF;
		public static MetadataToken nextTypeRefToken() {
			return new MetadataToken(TokenType.TypeRef, nextTokenRid--);
		}

		public static MetadataToken nextTypeDefToken() {
			return new MetadataToken(TokenType.TypeDef, nextTokenRid--);
		}

		public static MetadataToken nextFieldToken() {
			return new MetadataToken(TokenType.Field, nextTokenRid--);
		}

		public static MetadataToken nextMethodToken() {
			return new MetadataToken(TokenType.Method, nextTokenRid--);
		}

		public static MetadataToken nextPropertyToken() {
			return new MetadataToken(TokenType.Property, nextTokenRid--);
		}

		public static MetadataToken nextEventToken() {
			return new MetadataToken(TokenType.Event, nextTokenRid--);
		}

		public static TypeReference findTypeReference(ModuleDefinition module, string asmSimpleName, string fullName) {
			foreach (var type in module.GetTypeReferences()) {
				if (type.FullName != fullName)
					continue;
				var asmRef = type.Scope as AssemblyNameReference;
				if (asmRef == null || asmRef.Name != asmSimpleName)
					continue;

				return type;
			}
			return null;
		}

		public static TypeReference findOrCreateTypeReference(ModuleDefinition module, AssemblyNameReference asmRef, string ns, string name, bool isValueType) {
			var typeRef = findTypeReference(module, asmRef.Name, ns + "." + name);
			if (typeRef != null)
				return typeRef;

			typeRef = new TypeReference(ns, name, module, asmRef);
			typeRef.MetadataToken = nextTypeRefToken();
			typeRef.IsValueType = isValueType;
			return typeRef;
		}

		public static DN.TypeDefOrRefSig findOrCreateTypeReference(DN.ModuleDef module, DN.AssemblyRef asmRef, string ns, string name, bool isValueType) {
			var typeRef = module.UpdateRowId(new DN.TypeRefUser(module, ns, name, asmRef));
			if (isValueType)
				return new DN.ValueTypeSig(typeRef);
			else
				return new DN.ClassSig(typeRef);
		}

		public static FrameworkType getFrameworkType(ModuleDefinition module) {
			foreach (var modRef in module.AssemblyReferences) {
				if (modRef.Name != "mscorlib")
					continue;
				if (modRef.PublicKeyToken == null || modRef.PublicKeyToken.Length == 0)
					continue;
				switch (BitConverter.ToString(modRef.PublicKeyToken).Replace("-", "").ToLowerInvariant()) {
				case "b77a5c561934e089":
					return FrameworkType.Desktop;
				case "7cec85d7bea7798e":
					return FrameworkType.Silverlight;
				case "969db8053d3322ac":
					return FrameworkType.CompactFramework;
				case "1c9e259686f921e0":
					return FrameworkType.XNA;
				case "e92a8b81eba7ceb7":
					return FrameworkType.Zune;
				}
			}

			return FrameworkType.Unknown;
		}

		public static bool callsMethod(MethodDefinition method, string methodFullName) {
			if (method == null || method.Body == null)
				return false;

			foreach (var instr in method.Body.Instructions) {
				if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt && instr.OpCode.Code != Code.Newobj)
					continue;
				var calledMethod = instr.Operand as MethodReference;
				if (calledMethod == null)
					continue;
				if (calledMethod.FullName == methodFullName)
					return true;
			}

			return false;
		}

		public static bool callsMethod(MethodDefinition method, string returnType, string parameters) {
			if (method == null || method.Body == null)
				return false;

			foreach (var instr in method.Body.Instructions) {
				if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt && instr.OpCode.Code != Code.Newobj)
					continue;
				if (isMethod(instr.Operand as MethodReference, returnType, parameters))
					return true;
			}

			return false;
		}

		public static IList<Instruction> getArgPushes(IList<Instruction> instrs, int index) {
			return getArgPushes(instrs, ref index);
		}

		public static IList<Instruction> getArgPushes(IList<Instruction> instrs, ref int index) {
			if (index < 0 || index >= instrs.Count)
				return null;
			var startInstr = instrs[index];
			int pushes, pops;
			calculateStackUsage(startInstr, false, out pushes, out pops);

			index--;
			int numArgs = pops;
			var args = new List<Instruction>(numArgs);
			int stackSize = numArgs;
			while (index >= 0 && args.Count != numArgs) {
				var instr = instrs[index--];
				calculateStackUsage(instr, false, out pushes, out pops);
				if (instr.OpCode.Code == Code.Dup) {
					args.Add(instr);
					stackSize--;
				}
				else {
					if (pushes == 1)
						args.Add(instr);
					else if (pushes > 1)
						throw new NotImplementedException();
					stackSize -= pushes;

					if (pops != 0) {
						index++;
						if (getArgPushes(instrs, ref index) == null)
							return null;
					}
				}

				if (stackSize < 0)
					return null;
			}
			if (args.Count != numArgs)
				return null;
			args.Reverse();
			return args;
		}

		public static AssemblyNameReference addAssemblyReference(ModuleDefinition module, AssemblyNameReference asmRef) {
			foreach (var modAsmRef in module.AssemblyReferences) {
				if (modAsmRef.FullName == asmRef.FullName)
					return modAsmRef;
			}

			var newAsmRef = AssemblyNameReference.Parse(asmRef.FullName);
			module.AssemblyReferences.Add(newAsmRef);
			return newAsmRef;
		}

		public static ModuleReference addModuleReference(ModuleDefinition module, ModuleReference modRef) {
			foreach (var modModRef in module.ModuleReferences) {
				if (modModRef.Name == modRef.Name)
					return modModRef;
			}

			var newModRef = new ModuleReference(modRef.Name);
			module.ModuleReferences.Add(newModRef);
			return newModRef;
		}
	}
}
