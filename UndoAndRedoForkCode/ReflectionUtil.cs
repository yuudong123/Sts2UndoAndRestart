using System.Reflection;

namespace UndoAndRedoForkCode;

internal static class ReflectionUtil
{
    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Dictionary<(Type Type, string Name), FieldInfo?> FieldCache = new();
    private static readonly Dictionary<(Type Type, string Name), MethodInfo?> MethodCache = new();
    private static readonly Dictionary<(Type Type, string Name, string Signature), MethodInfo?>
        ExactMethodCache = new();

    public static FieldInfo? Field(Type type, string name)
    {
        (Type, string) key = (type, name);
        if (FieldCache.TryGetValue(key, out FieldInfo? cached))
        {
            return cached;
        }

        for (Type? current = type; current != null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(
                name,
                InstanceFlags | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                FieldCache[key] = field;
                return field;
            }
        }

        FieldCache[key] = null;
        return null;
    }

    public static MethodInfo? Method(Type type, string name)
    {
        (Type, string) key = (type, name);
        if (MethodCache.TryGetValue(key, out MethodInfo? cached))
        {
            return cached;
        }

        for (Type? current = type; current != null; current = current.BaseType)
        {
            MethodInfo[] methods = current
                .GetMethods(InstanceFlags | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == name)
                .ToArray();
            MethodInfo? method = methods.Length switch
            {
                0 => null,
                1 => methods[0],
                _ => methods.FirstOrDefault(candidate => candidate.GetParameters().Length == 0),
            };
            if (method != null)
            {
                MethodCache[key] = method;
                return method;
            }
        }

        MethodCache[key] = null;
        return null;
    }

    public static MethodInfo? Method(Type type, string name, params Type[] parameterTypes)
    {
        string signature = string.Join("|", parameterTypes.Select(type => type.FullName));
        (Type, string, string) key = (type, name, signature);
        if (ExactMethodCache.TryGetValue(key, out MethodInfo? cached))
        {
            return cached;
        }

        for (Type? current = type; current != null; current = current.BaseType)
        {
            MethodInfo? method = current.GetMethod(
                name,
                InstanceFlags | BindingFlags.DeclaredOnly,
                binder: null,
                types: parameterTypes,
                modifiers: null);
            if (method != null)
            {
                ExactMethodCache[key] = method;
                return method;
            }
        }

        ExactMethodCache[key] = null;
        return null;
    }

    public static T? GetField<T>(object owner, string name)
    {
        return Field(owner.GetType(), name)?.GetValue(owner) is T value
            ? value
            : default;
    }

    public static T? GetStaticField<T>(Type type, string name)
    {
        FieldInfo? field = type.GetField(
            name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return field?.GetValue(null) is T value
            ? value
            : default;
    }

    public static void SetField(object owner, string name, object? value)
    {
        Field(owner.GetType(), name)?.SetValue(owner, value);
    }

    public static void ReplaceList<T>(IList<T> destination, IEnumerable<T> source)
    {
        destination.Clear();
        foreach (T item in source)
        {
            destination.Add(item);
        }
    }
}
