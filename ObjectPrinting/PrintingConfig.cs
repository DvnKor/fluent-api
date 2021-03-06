﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace ObjectPrinting
{
    public class PrintingConfig<TOwner>
    {
        private readonly HashSet<Type> excludedTypes = new HashSet<Type>();
        private readonly HashSet<string> excludedPropertiesFullNames = new HashSet<string>();
        private readonly List<object> serializedObjects = new List<object>();
        private readonly Dictionary<Type, Delegate> customTypeSerializationMethods = new Dictionary<Type, Delegate>();

        private readonly Dictionary<string, Delegate> customPropertySerializationMethods =
            new Dictionary<string, Delegate>();

        private readonly Dictionary<Type, CultureInfo> customCultures = new Dictionary<Type, CultureInfo>();
        private readonly Dictionary<string, int> propertyMaxLength = new Dictionary<string, int>();

        private readonly Type[] finalTypes =
        {
            typeof(int), typeof(double), typeof(float), typeof(string),
            typeof(DateTime), typeof(TimeSpan)
        };


        public void SetCustomSerializationMethod<TPropType>(Func<TPropType, string> customMethod,
            Expression<Func<TOwner, TPropType>> memberSelector = null)
        {
            if (memberSelector == null)
            {
                customTypeSerializationMethods[typeof(TPropType)] = customMethod;
                return;
            }

            var memberExpression = memberSelector.Body as MemberExpression;
            var fullPropertyName = GetFullPropertyName(memberExpression);
            customPropertySerializationMethods[fullPropertyName] = customMethod;
        }

        public void SetCustomCulture<TPropType>(CultureInfo cultureInfo)
        {
            customCultures[typeof(TPropType)] = cultureInfo;
        }

        public void SetMaxLengthRestriction<TPropType>(Expression<Func<TOwner, TPropType>> memberSelector,
            int maxLength)
        {
            var memberExpression = memberSelector.Body as MemberExpression;
            var fullPropertyName = GetFullPropertyName(memberExpression);
            propertyMaxLength[fullPropertyName] = maxLength;
        }

        public PropertyPrintingConfig<TOwner, TPropType> Printing<TPropType>()
        {
            return new PropertyPrintingConfig<TOwner, TPropType>(this);
        }

        public PropertyPrintingConfig<TOwner, TPropType> Printing<TPropType>(
            Expression<Func<TOwner, TPropType>> memberSelector)
        {
            return new PropertyPrintingConfig<TOwner, TPropType>(this, memberSelector);
        }

        public PrintingConfig<TOwner> Excluding<TPropType>(Expression<Func<TOwner, TPropType>> memberSelector)
        {
            var memberExpression = memberSelector.Body as MemberExpression;
            excludedPropertiesFullNames.Add(GetFullPropertyName(memberExpression));
            return this;
        }

        private string GetFullPropertyName(Expression expression)
        {
            if (!(expression is MemberExpression memberExpression))
                return null;

            var prevName = GetFullPropertyName(memberExpression.Expression);
            return GetNextPropertyName(prevName, memberExpression.Member.Name);
        }

        internal PrintingConfig<TOwner> Excluding<TPropType>()
        {
            excludedTypes.Add(typeof(TPropType));
            return this;
        }

        public string PrintToString(TOwner obj)
        {
            return PrintToString(obj, 0, "");
        }

        private string PrintToString(object obj, int nestingLevel, string propertyName)
        {
            if (obj == null)
                return $"null{Environment.NewLine}";

            var objType = obj.GetType();

            if (IsSerialized(obj))
                return $"Cyclic Reference to {objType}{Environment.NewLine}";

            serializedObjects.Add(obj);

            if (excludedTypes.Contains(objType) || IsExcludedProperty(propertyName))
                return null;

            return finalTypes.Contains(objType)
                ? SerializeFinalType(obj, propertyName)
                : SerializeComplexType(obj, nestingLevel, propertyName);
        }

        private string SerializeComplexType(object obj, int nestingLevel, string currentPropertyName)
        {
            var objType = obj.GetType();
            var indentation = new string('\t', nestingLevel + 1);
            var sb = new StringBuilder();
            sb.AppendLine(objType.Name);

            foreach (var propertyInfo in objType.GetProperties())
            {
                var nextPropertyName = GetNextPropertyName(currentPropertyName, propertyInfo.Name);

                var propertyType = propertyInfo.PropertyType;

                if (IsExcludedProperty(propertyType, nextPropertyName))
                    continue;
                sb.Append($"{indentation}{propertyInfo.Name} = ");
                sb.Append(GetComplexTypeSerialization(propertyInfo, propertyType, currentPropertyName, nextPropertyName,
                    nestingLevel, obj));
            }

            return sb.ToString();
        }

        private static string GetNextPropertyName(string currentPropertyName, string nextPropertyName)
        {
            return string.IsNullOrEmpty(currentPropertyName)
                ? nextPropertyName
                : $"{currentPropertyName}.{nextPropertyName}";
        }


        private string GetComplexTypeSerialization(PropertyInfo propertyInfo, Type propertyType,
            string currentPropertyName,
            string nextPropertyName,
            int nestingLevel, object obj)
        {
            var customMethod = TryGetCustomSerializationMethod(propertyType, currentPropertyName);

            if (customMethod != null)
                return $"{SerializeWithCustomMethod(obj, propertyInfo, customMethod)}{Environment.NewLine}";

            if (!(propertyInfo.GetValue(obj) is ICollection collection))
                return PrintToString(propertyInfo.GetValue(obj), nestingLevel + 1, nextPropertyName);

            var elementsStrings = (from object element in collection
                select PrintToString(element, nestingLevel + 1, currentPropertyName).TrimEnd()).ToList();

            return $"[{string.Join(", ", elementsStrings)}]{Environment.NewLine}";
        }

        private bool IsExcludedProperty(Type propertyType, string nextPropertyName)
        {
            return excludedTypes.Contains(propertyType) || IsExcludedProperty(nextPropertyName);
        }

        private Delegate TryGetCustomSerializationMethod(Type propertyType, string propertyName)
        {
            Delegate customMethod = null;
            if (customTypeSerializationMethods.ContainsKey(propertyType))
            {
                customMethod = customTypeSerializationMethods[propertyType];
            }
            else if (customPropertySerializationMethods.ContainsKey(propertyName))
            {
                customMethod = customPropertySerializationMethods[propertyName];
            }

            return customMethod;
        }

        private string SerializeWithCustomMethod(object obj, PropertyInfo propertyInfo, Delegate method)
        {
            return (string) method.DynamicInvoke(propertyInfo.GetValue(obj));
        }

        private string SerializeFinalType(object obj, string propertyName)
        {
            var serialization = GetFinalTypeSerialization(obj, propertyName);
            if (propertyMaxLength.ContainsKey(propertyName))
            {
                serialization = serialization.Substring(0, propertyMaxLength[propertyName]);
            }

            return serialization + Environment.NewLine;
        }

        private string GetFinalTypeSerialization(object obj, string propertyName)
        {
            var objType = obj.GetType();
            if (customPropertySerializationMethods.ContainsKey(propertyName))
                return (string) customPropertySerializationMethods[propertyName].DynamicInvoke(obj);

            if (customTypeSerializationMethods.ContainsKey(objType))
                return (string) customTypeSerializationMethods[objType].DynamicInvoke(obj);

            var culture = TryGetCultureInfo(objType);
            if (culture != null)
                return Convert.ToString(obj, culture);

            return obj.ToString();
        }

        private CultureInfo TryGetCultureInfo(Type type)
        {
            return customCultures.ContainsKey(type) ? customCultures[type] : null;
        }

        private bool IsExcludedProperty(string checkedFullName)
        {
            return excludedPropertiesFullNames.Contains(checkedFullName);
        }

        private bool IsSerialized(object obj)
        {
            return serializedObjects.Any(serializedObject => ReferenceEquals(serializedObject, obj));
        }
    }
}