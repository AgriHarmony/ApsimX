﻿
namespace UnitTests
{
    using APSIM.Shared.Utilities;
    using System;
    using System.Data;
    using System.IO;
    using System.Reflection;

    public class Utilities
    {
        /// <summary>Call an event in a model</summary>
        public static void CallEvent(object model, string eventName, object[] arguments = null)
        {
            MethodInfo eventToInvoke = model.GetType().GetMethod("On" + eventName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (eventToInvoke != null)
            {
                if (arguments == null)
                    arguments = new object[] { model, new EventArgs() };
                eventToInvoke.Invoke(model, arguments);
            }
        }

        /// <summary>Inject a link into a model</summary>
        public static void InjectLink(object model, string linkFieldName, object linkFieldValue)
        {
            ReflectionUtilities.SetValueOfFieldOrProperty(linkFieldName, model, linkFieldValue);
        }


        /// <summary>Convert a SQLite table to a string.</summary>
        public static string TableToString(string fileName, string tableName)
        {
            SQLite database = new SQLite();
            database.OpenDatabase(fileName, true);
            DataTable data = database.ExecuteQuery("SELECT * FROM " + tableName);
            database.CloseDatabase();
            return TableToString(data);
        }

        /// <summary>Convert a DataTable to a string.</summary>
        public static string TableToString(DataTable data)
        {
            StringWriter writer = new StringWriter();
            DataTableUtilities.DataTableToText(data, 0, ",", true, writer);
            return writer.ToString();
        }


    }
}
