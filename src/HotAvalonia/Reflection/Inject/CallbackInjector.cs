using System.Reflection;
using System.Reflection.Emit;
using HotAvalonia.Helpers;

namespace HotAvalonia.Reflection.Inject;

/// <summary>
/// Provides methods to inject callbacks into methods at runtime.
/// </summary>
internal static class CallbackInjector
{
    /// <summary>
    /// The prefix for the name of the dynamic assembly generated by the injector.
    /// </summary>
    private const string AssemblyNamePrefix = "__Reflection.Inject.Dynamic";

    /// <summary>
    /// The name of the module within the dynamic assembly where the injected types reside.
    /// </summary>
    private const string ModuleName = "Inject";

    /// <summary>
    /// The prefix for the names of generated injection classes.
    /// </summary>
    private const string ClassNamePrefix = "Injection_";

    /// <summary>
    /// The prefix for the field that represents 'this' in the callback method.
    /// </summary>
    private const string ThisArgPrefix = "ThisArg_";

    /// <summary>
    /// The prefix for the field that stores the caller member metadata if it's required by the callback method.
    /// </summary>
    private const string CallerMemberPrefix = "CallerMember_";

    /// <summary>
    /// The prefix for the generated callback method.
    /// </summary>
    private const string CallbackPrefix = "Callback_";

    /// <summary>
    /// The module builder used to define dynamic types.
    /// </summary>
    private static readonly Lazy<ModuleBuilder> s_moduleBuilder = new(CreateModuleBuilder, isThreadSafe: true);

    /// <inheritdoc cref="Inject(MethodBase, MethodBase)"/>
    public static Type Inject(MethodBase target, Delegate callback)
    {
        _ = callback ?? throw new ArgumentNullException(nameof(callback));

        return Inject(target, callback.Method, callback.Target);
    }

    /// <inheritdoc cref="Inject(MethodBase, MethodBase, object?)"/>
    public static Type Inject(MethodBase target, MethodBase callback)
        => Inject(target, callback, thisArg: null);

    /// <summary>
    /// Injects a callback method into the specified target method.
    /// </summary>
    /// <param name="target">The target method where the callback will be injected.</param>
    /// <param name="callback">The callback method to inject.</param>
    /// <param name="thisArg">
    /// An instance of the object to be used when invoking instance method callbacks.
    /// Not applicable for static methods.
    /// </param>
    /// <returns>The dynamically generated type that contains the injected callback method.</returns>
    public static Type Inject(MethodBase target, MethodBase callback, object? thisArg)
    {
        _ = target ?? throw new ArgumentNullException(nameof(target));
        _ = callback ?? throw new ArgumentNullException(nameof(callback));
        _ = thisArg ?? (callback.IsStatic ? thisArg : throw new ArgumentNullException(nameof(thisArg)));

        MethodHelper.EnsureMethodSwappingIsAvailable();

        // Define
        string name = $"{ClassNamePrefix}{target.Name}_{Guid.NewGuid():N}";
        TypeBuilder injectionBuilder = s_moduleBuilder.Value.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);

        // Emit
        FieldBuilder? thisArgBuilder = !callback.IsStatic && thisArg is not null ? EmitThisArgField(injectionBuilder, thisArg.GetType()) : null;
        FieldBuilder? callerMemberBuilder = NeedsCallerMember(callback) ? EmitCallerMemberField(injectionBuilder, target.GetType()) : null;
        MethodBuilder callbackBuilder = EmitCallbackMethod(injectionBuilder, target, callback, thisArgBuilder, callerMemberBuilder);

        // Build
        Type injection = injectionBuilder.CreateTypeInfo();
        FieldInfo? thisArgField = thisArgBuilder is null ? null : injection.GetField(thisArgBuilder.Name, BindingFlags.NonPublic | BindingFlags.Static);
        FieldInfo? callerMemberField = callerMemberBuilder is null ? null : injection.GetField(callerMemberBuilder.Name, BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo callbackMethod = injection.GetMethod(callbackBuilder.Name) ?? throw new MissingMethodException(injection.FullName, callbackBuilder.Name);

        // Set up
        thisArgField?.SetValue(null, thisArg);
        callerMemberField?.SetValue(null, target);
        MethodHelper.OverrideMethod(target, callbackMethod);

        return injection;
    }

    /// <summary>
    /// Emits a field to hold an instance of the object to be used when invoking instance method callbacks.
    /// </summary>
    /// <param name="typeBuilder">The type builder.</param>
    /// <param name="type">The type of the field.</param>
    /// <returns>A field builder for the emitted field.</returns>
    private static FieldBuilder EmitThisArgField(TypeBuilder typeBuilder, Type type)
        => EmitField(typeBuilder, type, ThisArgPrefix);

    /// <summary>
    /// Emits a field to hold an instance of the caller member metadata.
    /// </summary>
    /// <param name="typeBuilder">The type builder.</param>
    /// <param name="type">The type of the field.</param>
    /// <returns>A field builder for the emitted field.</returns>
    private static FieldBuilder EmitCallerMemberField(TypeBuilder typeBuilder, Type type)
        => EmitField(typeBuilder, type, CallerMemberPrefix);

    /// <summary>
    /// Emits a field to hold an instance of the specified type, with the given prefix.
    /// </summary>
    /// <param name="typeBuilder">The type builder.</param>
    /// <param name="type">The type of the field.</param>
    /// <param name="prefix">The prefix for the field name.</param>
    /// <returns>A field builder for the emitted field.</returns>
    private static FieldBuilder EmitField(TypeBuilder typeBuilder, Type type, string prefix)
    {
        string name = $"{prefix}{type.Name}+{Guid.NewGuid():N}";
        return typeBuilder.DefineField(name, type, FieldAttributes.Private | FieldAttributes.Static);
    }

    /// <summary>
    /// Emits an intermediary method that can execute a callback and, conditionally, delegate the execution to the target method.
    /// </summary>
    /// <param name="typeBuilder">The type builder used for constructing the method.</param>
    /// <param name="target">The original target method that may be called by the emitted method.</param>
    /// <param name="callback">The callback method to be invoked by the emitted method.</param>
    /// <param name="thisArg">The optional 'this' argument if the callback is an instance method.</param>
    /// <param name="callerMember">The caller member metadata if it is required by the callback method.</param>
    /// <returns>A method builder for the emitted method.</returns>
    private static MethodBuilder EmitCallbackMethod(TypeBuilder typeBuilder, MethodBase target, MethodBase callback, FieldBuilder? thisArg, FieldBuilder? callerMember)
    {
        string name = $"{CallbackPrefix}{callback.Name}+{Guid.NewGuid():N}";

        MethodAttributes attributes = target.IsStatic ? MethodAttributes.Public | MethodAttributes.Static : MethodAttributes.Public;
        Type targetReturnType = target is MethodInfo targetMethodInfo ? targetMethodInfo.ReturnType : typeof(void);
        ParameterInfo[] targetParameters = target.GetParameters();
        Type[] targetParameterTypes = Array.ConvertAll(targetParameters, static x => x.ParameterType);
        MethodBuilder targetBuilder = typeBuilder.DefineMethod(name, attributes, target.CallingConvention, targetReturnType, targetParameterTypes);

        Type callbackReturnType = callback is MethodInfo callbackMethodInfo ? callbackMethodInfo.ReturnType : typeof(void);
        ParameterInfo[] callbackParameters = callback.GetParameters();

        ILGenerator il = targetBuilder.GetILGenerator();
        Label executeCallback = il.DefineLabel();
        if (targetReturnType != typeof(void))
            il.DeclareLocal(targetReturnType);

        // --------------- Execute the callback ---------------
        // ?ldsfld thisArg              // If not static
        // ldarg.0
        // ldarg.1
        // ...
        // ldarg N
        // ldc.i4/ldc.i8 GetFunctionPointer(callback)
        // calli
        // ?pop                         // return != typeof(bool) && return != typeof(void)
        // ?brfalse executeCallback     // return == typeof(bool)
        // ?ldloc.0                     // return == typeof(bool) && targetReturn != typeof(void)
        // ?ret                         // return == typeof(bool)
        if (!callback.IsStatic)
            il.Emit(OpCodes.Ldsfld, thisArg ?? throw new ArgumentNullException(nameof(thisArg)));

        ReadOnlySpan<ParameterInfo> availableArgs = targetParameters;
        foreach (ParameterInfo callbackParameter in callbackParameters)
            availableArgs = EmitCallbackParameter(il, callbackParameter, availableArgs, target, callerMember);

        il.EmitLdc_IN(MethodHelper.GetFunctionPointer(callback));
        il.EmitCalli(OpCodes.Calli, callback.CallingConvention, callbackReturnType, Array.ConvertAll(callbackParameters, static x => x.ParameterType), null);

        if (callbackReturnType == typeof(bool))
        {
            il.Emit(OpCodes.Brfalse, executeCallback);

            if (targetReturnType != typeof(void))
                il.Emit(OpCodes.Ldloc_0);

            il.Emit(OpCodes.Ret);
        }
        else if (callbackReturnType != typeof(void))
        {
            il.Emit(OpCodes.Pop);
        }
        // ----------------------------------------------------

        // ------- Redirect the call back to the target -------
        // ExecuteCallback:
        // ldarg.0
        // ldarg.1
        // ...
        // ldarg N
        // ldc.i4/ldc.i8 GetFunctionPointer(target)
        // calli
        // ret
        il.MarkLabel(executeCallback);

        int parameterCount = targetParameterTypes.Length + (target.IsStatic ? 0 : 1);
        for (int i = 0; i < parameterCount; ++i)
            il.EmitLdarg(i);

        il.EmitLdc_IN(MethodHelper.GetFunctionPointer(target));
        il.EmitCalli(OpCodes.Calli, target.CallingConvention, targetReturnType, targetParameterTypes, null);
        il.Emit(OpCodes.Ret);
        // ----------------------------------------------------

        return targetBuilder;
    }

    /// <summary>
    /// Emits the necessary IL instructions to load an argument for the callback method.
    /// </summary>
    /// <param name="il">The IL generator used for emitting instructions.</param>
    /// <param name="parameter">The parameter in the callback method being processed.</param>
    /// <param name="remainingArgs">The arguments of the calling method that has not been used yet.</param>
    /// <param name="target">The original target method that may be called by the callback.</param>
    /// <param name="callerMember">The caller member metadata if it is required by the callback method.</param>
    /// <returns>A read-only span of remaining arguments after this emission.</returns>
    private static ReadOnlySpan<ParameterInfo> EmitCallbackParameter(ILGenerator il, ParameterInfo parameter, ReadOnlySpan<ParameterInfo> remainingArgs, MethodBase target, FieldBuilder? callerMember)
    {
        Type parameterType = parameter.ParameterType;
        CallbackParameterType callbackParameterType = parameter.GetCallbackParameterType();
        switch (callbackParameterType)
        {
            case CallbackParameterType.CallbackResult when parameter.IsOut && target is MethodInfo targetInfo && targetInfo.ReturnType.IsAssignableFrom(parameterType.GetElementType()):
                il.Emit(OpCodes.Ldloca_S, 0);
                return remainingArgs;

            case CallbackParameterType.Caller when !target.IsStatic && parameterType.IsAssignableFrom(target.DeclaringType):
                il.Emit(OpCodes.Ldarg_0);
                return remainingArgs;

            case CallbackParameterType.CallerMember when callerMember is not null && parameterType.IsAssignableFrom(callerMember.FieldType):
                il.Emit(OpCodes.Ldsfld, callerMember);
                return remainingArgs;

            case CallbackParameterType.CallerMemberName when parameterType == typeof(string):
                il.Emit(OpCodes.Ldstr, target.Name);
                return remainingArgs;

            case not CallbackParameterType.None:
                il.EmitLddefault(parameterType);
                return remainingArgs;
        }

        while (true)
        {
            if (remainingArgs.IsEmpty)
                throw new ArgumentException("No suitable argument found for the callback parameter.", parameter.Name);

            ParameterInfo arg = remainingArgs[0];
            remainingArgs = remainingArgs.Slice(1);

            if (!parameterType.IsAssignableFrom(arg.ParameterType))
                continue;

            il.EmitLdarg(arg.Position + (target.IsStatic ? 0 : 1));
            return remainingArgs;
        }
    }

    /// <summary>
    /// Determines if a method requires a [CallerMember] parameter.
    /// </summary>
    /// <param name="method">The method to check.</param>
    /// <returns><c>true</c> if the method requires a caller member parameter; otherwise, <c>false</c>.</returns>
    private static bool NeedsCallerMember(MethodBase method)
        => method.GetParameters().Any(static x => x.GetCallbackParameterType() is CallbackParameterType.CallerMember);

    /// <summary>
    /// Creates and returns a dynamic module builder.
    /// </summary>
    /// <returns>A new instance of <see cref="ModuleBuilder"/>.</returns>
    private static ModuleBuilder CreateModuleBuilder()
    {
        string assemblyName = $"{AssemblyNamePrefix}+{Guid.NewGuid():N}";
        AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new(assemblyName), AssemblyBuilderAccess.RunAndCollect);
        return assemblyBuilder.DefineDynamicModule(ModuleName);
    }
}
