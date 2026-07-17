using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace UndoAndRestartCode;

internal sealed class ObjectGraphSnapshot
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new();
    private static int _hasLoggedRngObjectGraphFallback;

    private readonly object _root;
    private readonly Dictionary<FieldInfo, object?> _values = new();

    private ObjectGraphSnapshot(object root)
    {
        _root = root;
        Dictionary<object, object> visited = new(ReferenceEqualityComparer.Instance);
        foreach (FieldInfo field in EnumerateFields(root.GetType()))
        {
            if (ShouldSkipField(field))
            {
                continue;
            }

            _values[field] = Clone(field.GetValue(root), root, visited);
        }
    }

    public static ObjectGraphSnapshot Capture(object root)
    {
        return new ObjectGraphSnapshot(root);
    }

    public void Restore(object root)
    {
        if (!ReferenceEquals(root, _root))
        {
            throw new InvalidOperationException(
                $"Snapshot root changed from {_root.GetType().Name} to {root.GetType().Name}.");
        }

        Dictionary<object, object> visited = new(ReferenceEqualityComparer.Instance);
        foreach ((FieldInfo field, object? captured) in _values)
        {
            try
            {
                object? current = field.GetValue(root);
                if (field.IsInitOnly && TryRestoreContainer(current, captured, root, visited))
                {
                    continue;
                }

                field.SetValue(root, Clone(captured, root, visited));
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn(
                    $"Snapshot field restore failed: {root.GetType().Name}.{field.Name}: {ex.Message}");
            }
        }
    }

    public static object? CloneValue(object? value, object? owner = null)
    {
        return Clone(value, owner, new Dictionary<object, object>(ReferenceEqualityComparer.Instance));
    }

    private static object? Clone(
        object? value,
        object? owner,
        Dictionary<object, object> visited)
    {
        if (value == null)
        {
            return null;
        }

        Type type = value.GetType();
        if (IsAtomic(type) || IsIdentity(value) || ShouldPreserveReference(value))
        {
            return value;
        }

        if (visited.TryGetValue(value, out object? existing))
        {
            return existing;
        }

        if (value is Rng rng)
        {
            return CloneRng(rng, owner, visited);
        }

        if (value is CardEnergyCost energyCost && owner is CardModel card)
        {
            return energyCost.Clone(card);
        }

        if (value is DynamicVarSet vars && owner is AbstractModel model)
        {
            return vars.Clone(model);
        }

        if (type.IsArray)
        {
            Array source = (Array)value;
            Array copy = Array.CreateInstance(type.GetElementType()!, source.Length);
            visited[value] = copy;
            for (int i = 0; i < source.Length; i++)
            {
                copy.SetValue(Clone(source.GetValue(i), owner, visited), i);
            }

            return copy;
        }

        if (value is IDictionary dictionary)
        {
            IDictionary copy = CreateDictionary(type);
            visited[value] = copy;
            foreach (DictionaryEntry entry in dictionary)
            {
                copy.Add(Clone(entry.Key, owner, visited)!, Clone(entry.Value, owner, visited));
            }

            return copy;
        }

        if (TryCloneSet(value, type, owner, visited, out object? setCopy))
        {
            return setCopy;
        }

        if (value is IList list)
        {
            IList copy = CreateList(type);
            visited[value] = copy;
            foreach (object? item in list)
            {
                copy.Add(Clone(item, owner, visited));
            }

            return copy;
        }

        return CloneObjectByFields(value, owner, visited);
    }

    private static object CloneObjectByFields(
        object value,
        object? owner,
        Dictionary<object, object> visited)
    {
        Type type = value.GetType();
        object clone = MemberwiseCloneMethod.Invoke(value, null)!;
        visited[value] = clone;
        foreach (FieldInfo field in EnumerateFields(type))
        {
            if (ShouldSkipField(field))
            {
                continue;
            }

            object? child = field.GetValue(value);
            object? childClone = Clone(child, owner, visited);
            try
            {
                field.SetValue(clone, childClone);
            }
            catch
            {
                object? cloneChild = field.GetValue(clone);
                TryRestoreContainer(cloneChild, childClone, owner, visited);
            }
        }

        return clone;
    }

    private static Rng CloneRng(
        Rng rng,
        object? owner,
        Dictionary<object, object> visited)
    {
        Type rngType = rng.GetType();
        MethodInfo? toSerializable = rngType.GetMethod(
            "ToSerializable",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        if (toSerializable != null)
        {
            object serializable = toSerializable.Invoke(rng, null)
                ?? throw new InvalidOperationException("Rng.ToSerializable returned null.");
            ConstructorInfo? serializableConstructor = rngType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { serializable.GetType() },
                null);
            if (serializableConstructor != null)
            {
                Rng clone = (Rng)serializableConstructor.Invoke(new[] { serializable });
                visited[rng] = clone;
                return clone;
            }
        }

        PropertyInfo? seedProperty = rngType.GetProperty(
            "Seed",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        PropertyInfo? counterProperty = rngType.GetProperty(
            "Counter",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (seedProperty != null && counterProperty != null)
        {
            object seed = seedProperty.GetValue(rng)
                ?? throw new InvalidOperationException("Rng.Seed returned null.");
            object counter = counterProperty.GetValue(rng)
                ?? throw new InvalidOperationException("Rng.Counter returned null.");
            ConstructorInfo? legacyConstructor = rngType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { seed.GetType(), counter.GetType() },
                null);
            if (legacyConstructor != null)
            {
                Rng clone = (Rng)legacyConstructor.Invoke(new[] { seed, counter });
                visited[rng] = clone;
                return clone;
            }
        }

        if (Interlocked.Exchange(ref _hasLoggedRngObjectGraphFallback, 1) == 0)
        {
            MainFile.Logger.Warn(
                $"No constructor-based RNG snapshot API was found for {rngType.FullName}; " +
                "using object-graph cloning fallback.");
        }

        return (Rng)CloneObjectByFields(rng, owner, visited);
    }

    private static bool TryRestoreContainer(
        object? destination,
        object? source,
        object? owner,
        Dictionary<object, object> visited)
    {
        if (destination == null || source == null)
        {
            return false;
        }

        if (destination is IDictionary destinationDictionary &&
            source is IDictionary sourceDictionary)
        {
            destinationDictionary.Clear();
            foreach (DictionaryEntry entry in sourceDictionary)
            {
                destinationDictionary.Add(
                    Clone(entry.Key, owner, visited)!,
                    Clone(entry.Value, owner, visited));
            }

            return true;
        }

        if (destination is IList destinationList && source is IList sourceList)
        {
            destinationList.Clear();
            foreach (object? item in sourceList)
            {
                destinationList.Add(Clone(item, owner, visited));
            }

            return true;
        }

        Type destinationType = destination.GetType();
        if (destinationType.IsGenericType &&
            destinationType.GetGenericTypeDefinition() == typeof(HashSet<>) &&
            source.GetType() == destinationType)
        {
            destinationType.GetMethod("Clear")!.Invoke(destination, null);
            MethodInfo add = destinationType.GetMethod("Add")!;
            foreach (object? item in (IEnumerable)source)
            {
                add.Invoke(destination, new[] { Clone(item, owner, visited) });
            }

            return true;
        }

        if (!IsAtomic(destinationType) &&
            !IsIdentity(destination) &&
            !ShouldPreserveReference(destination) &&
            destinationType == source.GetType())
        {
            foreach (FieldInfo field in EnumerateFields(destinationType))
            {
                if (ShouldSkipField(field))
                {
                    continue;
                }

                object? sourceValue = field.GetValue(source);
                object? destinationValue = field.GetValue(destination);
                if (field.IsInitOnly &&
                    TryRestoreContainer(destinationValue, sourceValue, owner, visited))
                {
                    continue;
                }

                try
                {
                    field.SetValue(destination, Clone(sourceValue, owner, visited));
                }
                catch
                {
                    TryRestoreContainer(destinationValue, sourceValue, owner, visited);
                }
            }

            return true;
        }

        return false;
    }

    private static bool IsAtomic(Type type)
    {
        return type.IsPrimitive ||
               type.IsEnum ||
               type == typeof(string) ||
               type == typeof(Type) ||
               type == typeof(decimal) ||
               type == typeof(DateTime) ||
               type == typeof(DateTimeOffset) ||
               type == typeof(TimeSpan) ||
               type == typeof(Guid) ||
               type == typeof(Vector2) ||
               type == typeof(Vector2I) ||
               type == typeof(Vector3) ||
               type == typeof(Vector3I) ||
               type == typeof(Vector4) ||
               type == typeof(Color) ||
               type == typeof(StringName);
    }

    private static bool IsIdentity(object value)
    {
        return value is AbstractModel ||
               value is Player ||
               value is Creature ||
               value is CombatState ||
               value is RunState ||
               value is CardPile;
    }

    private static bool ShouldPreserveReference(object value)
    {
        return value is GodotObject ||
               value is Delegate ||
               value is Task ||
               value is CancellationTokenSource ||
               value is WaitHandle ||
               value is Type ||
               value.GetType().Namespace?.StartsWith("System.Reflection", StringComparison.Ordinal) == true;
    }

    private static bool ShouldSkipField(FieldInfo field)
    {
        return field.IsStatic ||
               typeof(Delegate).IsAssignableFrom(field.FieldType) ||
               typeof(Task).IsAssignableFrom(field.FieldType) ||
               typeof(GodotObject).IsAssignableFrom(field.FieldType) ||
               typeof(CancellationTokenSource).IsAssignableFrom(field.FieldType);
    }

    private static IEnumerable<FieldInfo> EnumerateFields(Type type)
    {
        if (FieldCache.TryGetValue(type, out FieldInfo[]? cached))
        {
            return cached;
        }

        List<FieldInfo> fields = new();
        for (Type? current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            fields.AddRange(current.GetFields(Fields | BindingFlags.DeclaredOnly));
        }

        FieldInfo[] result = fields.ToArray();
        FieldCache[type] = result;
        return result;
    }

    private static IDictionary CreateDictionary(Type type)
    {
        if (!type.IsInterface && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
        {
            return (IDictionary)Activator.CreateInstance(type)!;
        }

        Type[] arguments = type.IsGenericType
            ? type.GetGenericArguments()
            : new[] { typeof(object), typeof(object) };
        return (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments))!;
    }

    private static IList CreateList(Type type)
    {
        if (!type.IsArray &&
            !type.IsInterface &&
            !type.IsAbstract &&
            type.GetConstructor(Type.EmptyTypes) != null)
        {
            return (IList)Activator.CreateInstance(type)!;
        }

        Type itemType = type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
        return (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;
    }

    private static bool TryCloneSet(
        object source,
        Type type,
        object? owner,
        Dictionary<object, object> visited,
        out object? copy)
    {
        copy = null;
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(HashSet<>))
        {
            return false;
        }

        copy = Activator.CreateInstance(type)!;
        visited[source] = copy;
        MethodInfo add = type.GetMethod("Add")!;
        foreach (object? item in (IEnumerable)source)
        {
            add.Invoke(copy, new[] { Clone(item, owner, visited) });
        }

        return true;
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
