using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace JunimoServer.Util
{
    public static class ReflectionExtensions
    {
        public static IEnumerable<Type> GetTypesWithInterface(this Assembly assembly, Type interfaceType)
        {
            return assembly
                .GetTypes()
                .Where(type =>
                    interfaceType.IsAssignableFrom(type) &&
                    !type.IsAbstract &&
                    !type.IsGenericType);
        }

        public static IEnumerable<Type> GetTypesWithInterface<T>(this Assembly assembly)
        {
            return GetTypesWithInterface(assembly, typeof(T));
        }
    }
}
