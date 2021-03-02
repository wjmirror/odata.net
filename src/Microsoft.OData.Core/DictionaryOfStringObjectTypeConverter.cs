//---------------------------------------------------------------------
// <copyright file="DictionaryStringObjectTypeConverter.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Microsoft.OData.Edm;
using Microsoft.OData.Json;

namespace Microsoft.OData
{
    /// <summary>
    /// Handles serialization and deserialization for types derived from Geography.
    /// </summary>
    internal sealed class DictionaryOfStringObjectTypeConverter : IPrimitiveTypeConverter
    {
        /// <summary>
        /// Write the Atom representation of an instance of a primitive type to an XmlWriter.
        /// </summary>
        /// <param name="instance">The instance to write.</param>
        /// <param name="writer">The Xml writer to use to write the instance.</param>
        public void WriteAtom(object instance, XmlWriter writer)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Write the Atom representation of an instance of a primitive type to an TextWriter.
        /// </summary>
        /// <param name="instance">The instance to write.</param>
        /// <param name="writer">The text writer to use to write the instance.</param>
        public void WriteAtom(object instance, TextWriter writer)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Write the Json Lite representation of an instance of a primitive type to a json writer.
        /// </summary>
        /// <param name="instance">The instance to write.</param>
        /// <param name="jsonWriter">Instance of JsonWriter.</param>
        public void WriteJsonLight(object instance, IJsonWriter jsonWriter)
        {
            switch (instance)
            {
                case bool boolValue:
                    jsonWriter.WriteValue(boolValue);
                    return;
                case float floatValue:
                    jsonWriter.WriteValue(floatValue);
                    return;
                case short shortValue:
                    jsonWriter.WriteValue(shortValue);
                    return;
                case long longValue:
                    jsonWriter.WriteValue(longValue);
                    return;
                case double doubleValue:
                    jsonWriter.WriteValue(doubleValue);
                    return;
                case Guid guidValue:
                    jsonWriter.WriteValue(guidValue);
                    return;
                case decimal decimalValue:
                    jsonWriter.WriteValue(decimalValue);
                    return;
                case DateTimeOffset dateTimeOffsetValue:
                    jsonWriter.WriteValue(dateTimeOffsetValue);
                    return;
                case TimeSpan timeSpanValue:
                    jsonWriter.WriteValue(timeSpanValue);
                    return;
                case byte byteValue:
                    jsonWriter.WriteValue(byteValue);
                    return;
                case sbyte sbyteValue:
                    jsonWriter.WriteValue(sbyteValue);
                    return;
                case byte[] byteArrayValue:
                    jsonWriter.WriteValue(byteArrayValue);
                    return;
                case int intValue:
                    jsonWriter.WriteValue(intValue);
                    return;
                case string stringValue:
                    jsonWriter.WriteValue(stringValue);
                    return;
            }

            var type = instance.GetType();
#if NETSTANDARD1_1
            if (type.IsGenericType())
#else
            if (type.IsGenericType)
#endif
            {
                var isDictionary = type.GetGenericTypeDefinition() == typeof(Dictionary<,>);
                if (isDictionary)
                {
                    var enumerable = instance as System.Collections.IEnumerable;
                    jsonWriter.StartObjectScope();
                    foreach (KeyValuePair<string, object> item in enumerable)
                    {
                        jsonWriter.WriteName(item.Key);
                        WriteJsonLight(item.Value, jsonWriter);
                    }
                    jsonWriter.EndObjectScope();

                    return;
                }
            }

            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            {
                var enumerable = instance as System.Collections.IEnumerable;
                jsonWriter.StartArrayScope();
                foreach (var item in enumerable)
                {
                    WriteJsonLight(item, jsonWriter);
                }
                jsonWriter.EndArrayScope();
            }
        }
    }
}
