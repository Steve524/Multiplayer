using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Multiplayer.Common;

internal sealed class SyncTypeCache(Type type)
{
    private static readonly ConcurrentDictionary<Type, SyncTypeCache> Cache = new();
    private static readonly MethodInfo CreateListMethod = GetFactoryMethod(nameof(CreateListGeneric));
    private static readonly MethodInfo CreateDictionaryMethod = GetFactoryMethod(nameof(CreateDictionaryGeneric));

    private Type[]? arguments;
    private Type? genericDefinition;
    private Type? elementType;
    private Type? arrayType;
    private Type? listType;
    private Type? enumUnderlyingType;
    private ConstructorInfo? tupleConstructor;
    private FieldInfo[]? binaryTupleFields;
    private Func<int, IList>? listFactory;
    private Func<IDictionary>? dictionaryFactory;

    public static SyncTypeCache For(Type type) => Cache.GetOrAdd(type, static key => new SyncTypeCache(key));

    public Type[] Arguments => arguments ??= type.GetGenericArguments();
    public Type GenericDefinition => genericDefinition ??= type.GetGenericTypeDefinition();
    public Type ElementType => elementType ??= type.GetElementType()!;
    public Type ArrayType => arrayType ??= type.MakeArrayType();
    public Type ListType => listType ??= typeof(List<>).MakeGenericType(type);
    public Type EnumUnderlyingType => enumUnderlyingType ??= Enum.GetUnderlyingType(type);
    public ConstructorInfo TupleConstructor => tupleConstructor ??= type.GetConstructors().First();

    public FieldInfo[] BinaryTupleFields => binaryTupleFields ??=
        [type.GetField("Item1")!, type.GetField("Item2")!];

    public IList CreateList(int capacity) => (listFactory ??=
        (Func<int, IList>)CreateListMethod.MakeGenericMethod(Arguments).CreateDelegate(typeof(Func<int, IList>)))(capacity);

    public IDictionary CreateDictionary() => (dictionaryFactory ??=
        (Func<IDictionary>)CreateDictionaryMethod.MakeGenericMethod(Arguments).CreateDelegate(typeof(Func<IDictionary>)))();

    private static MethodInfo GetFactoryMethod(string name) =>
        typeof(SyncTypeCache).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static IList CreateListGeneric<T>(int capacity) => new List<T>(capacity);

    private static IDictionary CreateDictionaryGeneric<TKey, TValue>() where TKey : notnull =>
        new Dictionary<TKey, TValue>();
}
