﻿#region License
/*
Copyright (c) 2013-2014 Daniil Rodin of Buhgalteria.Kontur team of SKB Kontur

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using SharpRpc.Codecs;
using SharpRpc.Interaction;
using SharpRpc.Reflection;
using System.Linq;

namespace SharpRpc.ServerSide
{
    public class ServiceMethodDelegateFactory : IServiceMethodDelegateFactory
    {
        struct ParameterNecessity
        {
            public MethodParameterDescription Description;
            public IEmittingCodec Codec;
            public LocalBuilder LocalVariable;
        }

        // ReSharper disable once UnusedMember.Global
        public static Task<byte[]> ToEmptyByteArrayTask(Task task)
        {
            return task.ContinueWith(t => new byte[0]);
        }

        private static readonly Type[] ParameterTypes = { typeof(ICodecContainer), typeof(object), typeof (byte[]), typeof(int) };
        private static readonly Type[] FuncConstructorParameters = { typeof(object), typeof(IntPtr) };
        private static readonly MethodInfo TaskFromResultMethod = typeof(Task).GetMethod("FromResult").MakeGenericMethod(typeof(byte[]));
        private static readonly MethodInfo ToEmptyByteArrayTaskMethod = typeof(ServiceMethodDelegateFactory).GetMethod("ToEmptyByteArrayTask");
        private static readonly MethodInfo IntPtrFromLong = typeof(IntPtr).GetMethods().Single(x => x.Name == "op_Explicit" && x.GetParameters()[0].ParameterType == typeof(long));

        public ServiceMethodDelegate CreateMethodDelegate(ICodecContainer codecContainer, ServiceDescription serviceDescription, ServicePath servicePath, Type[] genericArguments)
        {
            var serviceInterface = serviceDescription.Type;
            var methodNameWithPath = serviceInterface.FullName + "__" + string.Join("_", servicePath);

            var dynamicMethod = new DynamicMethod("__srpc__handle__" + methodNameWithPath,
                typeof(Task<byte[]>), ParameterTypes, Assembly.GetExecutingAssembly().ManifestModule, true);
            var il = dynamicMethod.GetILGenerator();
            var emittingContext = new EmittingContext(il);

            il.Emit(OpCodes.Ldarg_1);                                           // stack_0 = (TServiceImplementation) arg_1
            il.Emit(OpCodes.Castclass, serviceInterface);

            var serviceDesc = serviceDescription;
            for (int i = 1; i < servicePath.Length - 1; i++)
            {
                var propertyInfo = serviceDesc.Type.GetProperty(servicePath[i]);
                il.Emit(OpCodes.Callvirt, propertyInfo.GetGetMethod());         // stack_0 = stack_0.Property
                if (!serviceDesc.TryGetSubservice(servicePath[i], out serviceDesc))
                    throw new InvalidPathException();
            }

            var methodName = servicePath.MethodName;
            MethodDescription methodDesc;
            if (!serviceDesc.TryGetMethod(methodName, out methodDesc))
                throw new InvalidPathException();

            var genericArgumentMap = methodDesc.GenericParameters.Zip(genericArguments, (p, a) => new KeyValuePair<string, Type>(p.Name, a)).ToDictionary(x => x.Key, x => x.Value);

            var parameters = methodDesc.Parameters.Select((x, i) => CreateParameterNecessity(methodDesc, i, codecContainer, genericArgumentMap, il)).ToArray();

            var requestParameters = parameters
                .Where(x => x.Description.Way == MethodParameterWay.Val || x.Description.Way == MethodParameterWay.Ref)
                .ToArray();

            var responseParameters = parameters
                .Where(x => x.Description.Way == MethodParameterWay.Ref || x.Description.Way == MethodParameterWay.Out)
                .ToArray();

            var retvalType = methodDesc.ReturnType.DeepSubstituteGenerics(genericArgumentMap);

            if (requestParameters.Any())
            {
                il.Emit_Ldarg(2);                                           // remainingBytes = dataArray.Length - offset
                il.Emit(OpCodes.Ldlen);
                il.Emit_Ldarg(3);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Stloc, emittingContext.RemainingBytesVar);
                var pinnedVar = il.Emit_PinArray(typeof(byte), 2);         // var pinned dataPointer = pin(dataArray)
                il.Emit(OpCodes.Ldloc, pinnedVar);                         // data = dataPointer + offset
                il.Emit_Ldarg(3);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, emittingContext.DataPointerVar);
            }

            foreach (var parameter in parameters)
            {
                switch (parameter.Description.Way)
                {
                    case MethodParameterWay.Val:
                        parameter.Codec.EmitDecode(emittingContext, false); // stack_i = decode(data, remainingBytes, false)
                        break;
                    case MethodParameterWay.Ref:
                        parameter.Codec.EmitDecode(emittingContext, false); // param_i = decode(data, remainingBytes, false)
                        il.Emit(OpCodes.Stloc, parameter.LocalVariable);
                        il.Emit(OpCodes.Ldloca, parameter.LocalVariable);   // stack_i = *param_i
                        break;
                    case MethodParameterWay.Out:
                        il.Emit(OpCodes.Ldloca, parameter.LocalVariable);   // stack_i = *param_i
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            var resolvedMethodInfo = genericArguments.Any()
                ? methodDesc.MethodInfo.MakeGenericMethod(genericArguments)
                : methodDesc.MethodInfo;
            il.Emit(OpCodes.Callvirt, resolvedMethodInfo);                 // stack_0 = stack_0.Method(stack_1, stack_2, ...)

            switch (methodDesc.RemotingType)
            {
                case MethodRemotingType.Direct:
                    EmitProcessAndEncodeDirect(emittingContext, codecContainer, responseParameters, retvalType);
                    break;
                case MethodRemotingType.AsyncVoid:
                    EmitProcessAndEncodeAsyncVoid(emittingContext);
                    break;
                case MethodRemotingType.AsyncWithRetval:
                    EmitProcessAndEncodeAsyncWithRetval(emittingContext, codecContainer, methodNameWithPath, retvalType.GetGenericArguments()[0]);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return (ServiceMethodDelegate)dynamicMethod.CreateDelegate(typeof(ServiceMethodDelegate));
        }

        private static ParameterNecessity CreateParameterNecessity(MethodDescription methodDesc, int parameterIndex, ICodecContainer codecContainer, Dictionary<string, Type> genericArgumentMap, ILGenerator il)
        {
            var parameterDesc = methodDesc.Parameters[parameterIndex];
            var resolvedType = parameterDesc.Type.DeepSubstituteGenerics(genericArgumentMap);
            return new ParameterNecessity
            {
                Description = parameterDesc,
                Codec = codecContainer.GetEmittingCodecFor(resolvedType),
                LocalVariable = parameterDesc.Way != MethodParameterWay.Val ? il.DeclareLocal(resolvedType) : null
            };
        }

        private static void EmitProcessAndEncodeDirect(IEmittingContext emittingContext, ICodecContainer codecContainer, ParameterNecessity[] responseParameters, Type retvalType)
        {
            var il = emittingContext.IL;
            EmitEncodeDirect(emittingContext, codecContainer, responseParameters, retvalType);
            il.Emit(OpCodes.Call, TaskFromResultMethod);
            il.Emit(OpCodes.Ret);
        }

        private static void EmitProcessAndEncodeAsyncVoid(IEmittingContext emittingContext)
        {
            var il = emittingContext.IL;
            il.Emit(OpCodes.Call, ToEmptyByteArrayTaskMethod);
            il.Emit(OpCodes.Ret);
        }

        private static void EmitProcessAndEncodeAsyncWithRetval(IEmittingContext emittingContext, ICodecContainer codecContainer, string methodNameWithPath, Type pureRetvalType)
        {
            var encodeDeferredMethod = CreateEncodeDeferredMethod(codecContainer, methodNameWithPath, pureRetvalType);
            var funcType = GetDeferredMethodFuncType(pureRetvalType);
            var continueWithMethod = GetTaskContinueWithMethod(pureRetvalType);
            
            var il = emittingContext.IL;
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldc_I8, (long)DynamicMethodPointerExtractor.ExtractPointer(encodeDeferredMethod));
            il.Emit(OpCodes.Call, IntPtrFromLong);
            il.Emit(OpCodes.Newobj, funcType.GetConstructor(FuncConstructorParameters));
            il.Emit(OpCodes.Callvirt, continueWithMethod);
            il.Emit(OpCodes.Ret);
        }

        private static MethodInfo GetTaskContinueWithMethod(Type pureRetvalType)
        {
            var typeofTask = typeof(Task<>).MakeGenericType(pureRetvalType);
            return typeofTask.GetMethods().Single(IsCorrectContinueWith).MakeGenericMethod(typeof(byte[]));
        }

        private static bool IsCorrectContinueWith(MethodInfo methodInfo)
        {
            if (methodInfo.Name != "ContinueWith")
                return false;

            var parameters = methodInfo.GetParameters();
            if (parameters.Length != 1)
                return false;

            var parameterType = parameters[0].ParameterType;
            if (parameterType.GetGenericTypeDefinition() != typeof(Func<,>))
                return false;

            if (!parameterType.GetGenericArguments()[0].IsGenericType)
                return false;

            return true;
        }

        private static Type GetDeferredMethodFuncType(Type pureRetvalType)
        {
            var typeofTask = typeof(Task<>).MakeGenericType(pureRetvalType);
            return typeof(Func<,>).MakeGenericType(new[] { typeofTask, typeof(byte[]) });
        }

        private static DynamicMethod CreateEncodeDeferredMethod(ICodecContainer codecContainer, string methodNameWithPath, Type pureRetvalType)
        {
            var dynamicMethod = new DynamicMethod(
                "__srpc__handle_" + methodNameWithPath + "__EncodeDeferred",
                typeof(byte[]), new [] { typeof(Task<>).MakeGenericType(pureRetvalType) }, Assembly.GetExecutingAssembly().ManifestModule, true);
            var il = dynamicMethod.GetILGenerator();
            var emittingContext = new EmittingContext(il);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(Task<>).MakeGenericType(pureRetvalType).GetMethod("get_Result"));
            EmitEncodeDirect(emittingContext, codecContainer, new ParameterNecessity[0], pureRetvalType);
            il.Emit(OpCodes.Ret);
            return dynamicMethod;
        }

        private static void EmitEncodeDirect(IEmittingContext emittingContext, ICodecContainer codecContainer, ParameterNecessity[] responseParameters, Type retvalType)
        {
            var il = emittingContext.IL;
            bool hasRetval = retvalType != typeof(void);

            if (hasRetval || responseParameters.Any())
            {
                IEmittingCodec retvalCodec = null;
                LocalBuilder retvalVar = null;

                if (hasRetval)
                {
                    retvalCodec = codecContainer.GetEmittingCodecFor(retvalType);
                    retvalVar = il.DeclareLocal(retvalType);                                        // var ret = stack_0
                    il.Emit(OpCodes.Stloc, retvalVar);
                    retvalCodec.EmitCalculateSize(emittingContext, retvalVar);                      // stack_0 = calculateSize(ret)
                }

                bool hasSizeOnStack = hasRetval;
                foreach (var parameter in responseParameters)
                {
                    parameter.Codec.EmitCalculateSize(emittingContext, parameter.LocalVariable);    // stack_0 += calculateSize(param_i)
                    if (hasSizeOnStack)
                        il.Emit(OpCodes.Add);
                    else
                        hasSizeOnStack = true;
                }

                var dataArrayVar = il.DeclareLocal(typeof(byte[]));                         // var dataArray = new byte[size of retval]
                il.Emit(OpCodes.Newarr, typeof(byte));
                il.Emit(OpCodes.Stloc, dataArrayVar);

                var pinnedVar = il.Emit_PinArray(typeof(byte), dataArrayVar);               // var pinned dataPointer = pin(dataArrayVar)
                il.Emit(OpCodes.Ldloc, pinnedVar);                                          // data = dataPointer
                il.Emit(OpCodes.Stloc, emittingContext.DataPointerVar);

                foreach (var parameter in responseParameters)
                    parameter.Codec.EmitEncode(emittingContext, parameter.LocalVariable);   // encode(data, param_i)

                if (hasRetval)
                    retvalCodec.EmitEncode(emittingContext, retvalVar);                     // encode(data, ret)

                il.Emit(OpCodes.Ldloc, dataArrayVar);                                       // stack_0 = dataArray
            }
            else
            {
                il.Emit(OpCodes.Ldc_I4, 0);                                                 // stack_0 = new byte[0]
                il.Emit(OpCodes.Newarr, typeof(byte));
            }
        }

        private Task<byte[]> __ProcessAndEncodeAsync(Task<int> methodResult)
        {
            return methodResult.ContinueWith((Func<Task<int>, byte[]>)__EncodeDeferred);
        }

        private byte[] __EncodeDeferred(Task<int> metodResult)
        {
            return new byte[] { 1, 2, 3, 4 };
        }
    }
}