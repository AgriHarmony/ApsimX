﻿// -----------------------------------------------------------------------
// <copyright file="Links.cs" company="APSIM Initiative">
//     Copyright (c) APSIM Initiative
// </copyright>
// -----------------------------------------------------------------------
namespace Models.Core
{
    using APSIM.Shared.Utilities;
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;

    /// <summary>
    /// 
    /// </summary>
    public class Links
    {
        /// <summary>A collection of services that can be linked to</summary>
        private List<object> services;

        /// <summary>Constructor</summary>
        /// <param name="linkableServices">A collection of services that can be linked to</param>
        public Links(IEnumerable<object> linkableServices = null)
        {
            if (linkableServices != null)
                services = linkableServices.ToList();
            else
                services = new List<object>();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="rootNode"></param>
        /// <param name="recurse">Recurse through all child models?</param>
        public void Resolve(IModel rootNode, bool recurse = true)
        {
            if (recurse)
            {
                List<IModel> allModels = Apsim.ChildrenRecursively(rootNode);
                foreach (IModel modelNode in allModels)
                    ResolveInternal(modelNode, null);
            }
            else
                ResolveInternal(rootNode, null);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="rootNode"></param>
        public void Resolve(ModelWrapper rootNode)
        {
            List<ModelWrapper> allModels = rootNode.ChildrenRecursively;
            foreach (ModelWrapper modelNode in allModels)
                ResolveInternal(modelNode, allModels);
        }

        /// <summary>
        /// Resolve links in an unknown object e.g. user interface presenter
        /// </summary>
        /// <param name="obj"></param>
        public void Resolve(object obj)
        {
            // Go looking for [Link]s
            foreach (FieldInfo field in ReflectionUtilities.GetAllFields(
                                                            GetModel(obj).GetType(),
                                                            BindingFlags.Instance | BindingFlags.FlattenHierarchy | BindingFlags.NonPublic | BindingFlags.Public))
            {
                LinkAttribute link = GetLinkAttribute(field);

                if (link != null)
                {
                    // For now only try matching on a service
                    object match = services.Find(s => field.FieldType.IsAssignableFrom(s.GetType()));
                    if (match != null)
                        field.SetValue(GetModel(obj), GetModel(match));
                    else
                        throw new Exception("Cannot find a match for link " + field.Name + " in model " + GetFullName(obj));
                }
            }
        }

        /// <summary>
        /// Set to null all link fields in the specified model.
        /// </summary>
        /// <param name="model">The model to look through for links</param>
        public void Unresolve(IModel model)
        {
            foreach (IModel modelNode in Apsim.ChildrenRecursively(model))
            {
                // Go looking for private [Link]s
                foreach (FieldInfo field in ReflectionUtilities.GetAllFields(
                                                model.GetType(),
                                                BindingFlags.Instance | BindingFlags.FlattenHierarchy | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    LinkAttribute link = ReflectionUtilities.GetAttribute(field, typeof(LinkAttribute), false) as LinkAttribute;
                    if (link != null)
                        field.SetValue(model, null);
                }
            }
        }

        /// <summary>
        /// Internal [link] resolution algorithm.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="allModels">A collection of all model wrappers</param>
        private void ResolveInternal(object obj, List<ModelWrapper> allModels)
        {
            // Go looking for [Link]s
            foreach (FieldInfo field in ReflectionUtilities.GetAllFields(
                                                            GetModel(obj).GetType(),
                                                            BindingFlags.Instance | BindingFlags.FlattenHierarchy | BindingFlags.NonPublic | BindingFlags.Public))
            {
                LinkAttribute link = GetLinkAttribute(field);

                if (link != null)
                {
                    // Get the field type or the array element if it is an array field.
                    Type fieldType = field.FieldType;
                    if (fieldType.IsArray)
                        fieldType = fieldType.GetElementType();
                    else if (field.FieldType.Name.StartsWith("List") && field.FieldType.GenericTypeArguments.Length == 1)
                        fieldType = field.FieldType.GenericTypeArguments[0];

                    // Try and get a match from our services first.
                    List<object> matches;
                    matches = services.FindAll(s => fieldType.IsAssignableFrom(s.GetType()));

                    // If no match on services then try other options.
                    if (matches.Count == 0 && obj is IModel)
                    {
                        Simulation parentSimulation = Apsim.Parent(obj as IModel, typeof(Simulation)) as Simulation;
                        if (fieldType.IsAssignableFrom(typeof(ILocator)) && parentSimulation != null)
                            matches.Add(new Locator(obj as IModel));
                        else if (fieldType.IsAssignableFrom(typeof(IEvent)) && parentSimulation != null)
                            matches.Add(new Events(obj as IModel));
                    }

                    // If no match on services then try other options.
                    if (matches.Count == 0)
                    {
                        // Get a list of models that could possibly match.
                        if (link is ParentLinkAttribute)
                        {
                            matches = new List<object>();
                            matches.Add(GetParent(obj, fieldType));
                        }
                        else if (link.IsScoped(field))
                            matches = GetModelsInScope(obj, allModels);
                        else
                            matches = GetChildren(obj);
                    }

                    // Filter possible matches to those of the correct type.
                    matches.RemoveAll(match => !fieldType.IsAssignableFrom(GetModel(match).GetType()));

                    // If we should use name to match then filter matches to those with a matching name.
                    if (link.UseNameToMatch(field))
                        matches.RemoveAll(match => !StringUtilities.StringsAreEqual(GetName(match), field.Name));

                    if (field.FieldType.IsArray)
                    {
                        Array array = Array.CreateInstance(fieldType, matches.Count);
                        for (int i = 0; i < matches.Count; i++)
                            array.SetValue(GetModel(matches[i]), i);
                        field.SetValue(GetModel(obj), array);
                        
                    }
                    else if (field.FieldType.Name.StartsWith("List") && field.FieldType.GenericTypeArguments.Length == 1)
                    {
                        var listType = typeof(List<>);
                        var constructedListType = listType.MakeGenericType(fieldType);
                        IList array = Activator.CreateInstance(constructedListType) as IList;
                        for (int i = 0; i < matches.Count; i++)
                            array.Add(GetModel(matches[i]));
                        field.SetValue(GetModel(obj), array);
                    }
                    else if (matches.Count == 0)
                    {
                        if (!link.IsOptional)
                            throw new Exception("Cannot find a match for link " + field.Name + " in model " + GetFullName(obj));
                    }
                    else if (matches.Count >= 2 && !link.IsScoped(field))
                        throw new Exception(string.Format(": Found {0} matches for link {1} in model {2} !", matches.Count, field.Name, GetFullName(obj)));
                    else
                        field.SetValue(GetModel(obj), GetModel(matches[0]));
                }
            }
        }

        /// <summary>
        /// Go looking for a link attribute.
        /// </summary>
        /// <param name="field">The associated field.</param>
        /// <returns>Returns link or null if none field on specified field.</returns>
        private static LinkAttribute GetLinkAttribute(FieldInfo field)
        {
            var attributes = field.GetCustomAttributes();
            foreach (Attribute attribute in attributes)
            {
                LinkAttribute link = attribute as LinkAttribute;
                if (link != null)
                    return link;
            }
            return null;
        }

        /// <summary>
        /// Determine the type of an object and return its model.
        /// </summary>
        /// <param name="obj">obj can be either a ModelWrapper or an IModel.</param>
        /// <returns>The model</returns>
        private object GetModel(object obj)
        {
            if (obj is IModel)
                return obj;
            else if (obj is ModelWrapper)
                return (obj as ModelWrapper).Model;
            else
                return obj;
        }

        /// <summary>
        /// Determine the type of an object and return its name.
        /// </summary>
        /// <param name="obj">obj can be either a ModelWrapper or an IModel.</param>
        /// <returns>The name</returns>
        private string GetName(object obj)
        {
            if (obj is IModel)
                return (obj as IModel).Name;
            else
                return (obj as ModelWrapper).Name;
        }

        /// <summary>
        /// Determine the type of an object and return its parent of the specified type.
        /// </summary>
        /// <param name="obj">obj can be either a ModelWrapper or an IModel.</param>
        /// <param name="type">The type of parent to find.</param>
        /// <returns>The matching parent</returns>
        private object GetParent(object obj, Type type)
        {
            if (obj is IModel)
                return Apsim.Parent(obj as IModel, type);
            else if (obj is ModelWrapper && (obj as ModelWrapper).Model is IModel)
                return Apsim.Parent((obj as ModelWrapper).Model as IModel, type);
            else
                throw new NotImplementedException();
        }

        /// <summary>
        /// Determine the type of an object and return its name.
        /// </summary>
        /// <param name="obj">obj can be either a ModelWrapper or an IModel.</param>
        /// <returns>The name</returns>
        private string GetFullName(object obj)
        {
            if (obj is IModel)
                return Apsim.FullPath(obj as IModel);
            else if (obj is ModelWrapper)
                return (obj as ModelWrapper).Name;
            else
                return obj.GetType().FullName;
        }

        /// <summary>
        /// Determine the type of an object and return all models that are in scope.
        /// </summary>
        /// <param name="obj">obj can be either a ModelWrapper or an IModel.</param>
        /// <param name="allModels">A collection of all models</param>
        /// <returns>The models that are in scope of obj.</returns>
        private List<object> GetModelsInScope(object obj, List<ModelWrapper> allModels)
        {
            if (obj is IModel)
                return Apsim.FindAll(obj as IModel).Cast<object>().ToList();
            else
                return (obj as ModelWrapper).FindModelsInScope(allModels).Cast<object>().ToList();
        }

        /// <summary>
        /// Determine the type of an object and return all direct child models
        /// </summary>
        /// <param name="obj">obj can be either a ModelWrapper or an IModel.</param>
        /// <returns>The child models.</returns>
        private List<object> GetChildren(object obj)
        {
            if (obj is IModel)
                return (obj as IModel).Children.Cast<object>().ToList();
            else
                return (obj as ModelWrapper).Children.Cast<object>().ToList();
        }



    }
}
