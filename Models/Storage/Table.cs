﻿using APSIM.Shared.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Serialization;

namespace Models.Storage
{
    /// <summary>Encapsulates a table that needs writing to the database.</summary>
    class Table
    {
        public class Column
        {
            public string Name { get; private set; }
            public string Units { get; private set; }
            public string SQLiteDataType { get; set; }

            /// <summary>Constructor</summary>
            /// <param name="columnName">Name of column</param>
            /// <param name="columnUnits">Units of column</param>
            /// <param name="dataTypeString">Data type of column</param>
            public Column(string columnName, string columnUnits, string dataTypeString)
            {
                Name = columnName;
                Units = columnUnits;
                SQLiteDataType = dataTypeString;
            }
        }

        /// <summary>Rows to write to .db file</summary>
        private List<object[]> RowsToWrite = new List<object[]>();

        /// <summary>A set of column names for quickly checking if columns exist in this table.</summary>
        private SortedSet<string> sortedColumnNames = new SortedSet<string>();

        /// <summary>Name of table.</summary>
        public string Name { get; private set; }

        /// <summary>Are there any rows that need writing?</summary>
        public bool HasRowsToWrite { get { return RowsToWrite.Count > 0; } }

        /// <summary>Column names in table</summary>
        public List<Column> Columns { get; private set; }

        /// <summary>Constructor</summary>
        /// <param name="tableName">Name of table</param>
        public Table(string tableName)
        {
            Name = tableName;
            Columns = new List<Column>();
            Columns.Add(new Column("SimulationID", null, "integer"));
        }

        /// <summary>Add a row to our list of rows to write</summary>
        /// <param name="simulationID"></param>
        /// <param name="rowColumnNames"></param>
        /// <param name="rowColumnUnits"></param>
        /// <param name="rowValues"></param>
        public void AddRow(int simulationID, IEnumerable<string> rowColumnNames, IEnumerable<string> rowColumnUnits, IEnumerable<object> rowValues)
        {
            // We want all rows to be a normalised flat table. All rows must have the same number of values
            // and be in correct order i.e. like a .NET DataTable.

            // Firstly flatten our arrays and structures from the rowValues passed in.
            Flatten(ref rowColumnNames, ref rowColumnUnits, ref rowValues);

            // Ensure the row's columns are in this table's columns.
            for (int i = 0; i < rowColumnNames.Count(); i++)
                if (!sortedColumnNames.Contains(rowColumnNames.ElementAt(i)))
                {
                    sortedColumnNames.Add(rowColumnNames.ElementAt(i));
                    object value = rowValues.ElementAt(i);
                    string dataType = GetSQLiteDataType(value);
                    Columns.Add(new Column(rowColumnNames.ElementAt(i), 
                                           rowColumnUnits.ElementAt(i),
                                           dataType));

                    // Add extra column to all rows currently in table
                    for (int rowIndex = 0; rowIndex < RowsToWrite.Count; rowIndex++)
                    {
                        object[] values = RowsToWrite[rowIndex];
                        Array.Resize(ref values, values.Length + 1);
                        RowsToWrite[rowIndex] = values;
                    }
                }

            // Add new row to our values in correct order.
            object[] newRow = new object[Columns.Count];
            newRow[0] = simulationID;
            for (int i = 0; i < rowColumnNames.Count(); i++)
            {
                int columnIndex = Columns.FindIndex(column => column.Name == rowColumnNames.ElementAt(i));
                newRow[columnIndex] = rowValues.ElementAt(i);
                if (Columns[columnIndex].SQLiteDataType == null)
                    Columns[columnIndex].SQLiteDataType = GetSQLiteDataType(newRow[columnIndex]);
            }
            RowsToWrite.Add(newRow);
        }

        /// <summary>Set the connection</summary>
        /// <param name="connection">The SQLite connection</param>
        public void SetConnection(SQLite connection)
        {
            Open(connection);
        }

        /// <summary>Open the table and get a list of columns and simulation ids.</summary>
        /// <param name="connection">The SQLite connection to open</param>
        private void Open(SQLite connection)
        {
            Columns.Clear();
            DataTable data = connection.ExecuteQuery("pragma table_info('" + Name + "')");
            foreach (DataRow row in data.Rows)
            {
                string columnName = row["Name"].ToString();
                string units = null;
                units = LookupUnitsForColumn(connection, columnName);
                Columns.Add(new Column(columnName, units, null));
            }
        }

        /// <summary>Write the specified number of rows.</summary>
        /// <param name="connection">The SQLite connection to write to</param>
        /// <param name="simulationIDs">A dictionary of simulation IDs</param>
        public void WriteRows(SQLite connection, Dictionary<string, int> simulationIDs)
        { 
            IntPtr preparedInsertQuery = IntPtr.Zero;
            try
            {
                // If the table exists, make sure it has the required columns, otherwise create the table
                if (TableExists(connection, Name))
                    AlterTable(connection);
                else
                    CreateTable(connection);

                // Create an insert query
                preparedInsertQuery = CreateInsertQuery(connection);

                for (int rowIndex = 0; rowIndex < RowsToWrite.Count; rowIndex++)
                    connection.BindParametersAndRunQuery(preparedInsertQuery, RowsToWrite[rowIndex]);
            }
            finally
            {
                if (preparedInsertQuery != IntPtr.Zero)
                    connection.Finalize(preparedInsertQuery);
            }
        }

        /// <summary>Merge columns</summary>
        public void MergeColumns(Table table)
        {
            foreach (Column column in table.Columns)
            {
                if (Columns.Find(c => c.Name == column.Name) == null)
                    Columns.Add(column);
            }
        }

        /// <summary>Does the specified table exist?</summary>
        /// <param name="connection">SQLite connection</param>
        /// <param name="tableName">The table name to look for</param>
        private bool TableExists(SQLite connection, string tableName)
        {
            List<string> tableNames = DataTableUtilities.GetColumnAsStrings(connection.ExecuteQuery("SELECT * FROM sqlite_master"), "Name").ToList();
            return tableNames.Contains(tableName);
        }

        /// <summary>Lookup and return units for the specified column.</summary>
        /// <param name="connection">SQLite connection</param>
        /// <param name="columnName">The column name to return units for</param>
        private string LookupUnitsForColumn(SQLite connection, string columnName)
        {
            StringBuilder sql = new StringBuilder();
            sql.Append("SELECT Units FROM _Units WHERE TableName='");
            sql.Append(Name);
            sql.Append("' AND ColumnHeading='");
            sql.Append(columnName);
            sql.Append("'");
            DataTable data = connection.ExecuteQuery(sql.ToString());
            if (data.Rows.Count > 0)
                return (string)data.Rows[0][0];
            else
                return null;
        }

        /// <summary>Ensure columns exist in .db file</summary>
        /// <param name="connection">The SQLite connection to write to</param>
        private void CreateTable(SQLite connection)
        {
            StringBuilder sql = new StringBuilder();
            foreach (Column col in Columns)
            {
                if (sql.Length > 0)
                    sql.Append(',');

                sql.Append("[");
                sql.Append(col.Name);
                sql.Append("] ");
                if (col.SQLiteDataType == null)
                    sql.Append("integer");
                else
                    sql.Append(col.SQLiteDataType);
            }
            sql.Insert(0, "CREATE TABLE " + Name + " (");
            sql.Append(')');
            connection.ExecuteNonQuery(sql.ToString());
        }

        /// <summary>Alter an existing table ensuring all columns exist.</summary>
        /// <param name="connection">The SQLite connection to write to</param>
        private void AlterTable(SQLite connection)
        {
            DataTable columnData = connection.ExecuteQuery("pragma table_info('" + Name + "')");
            List<string> existingColumns = DataTableUtilities.GetColumnAsStrings(columnData, "Name").ToList();

            foreach (Column col in Columns)
            {
                if (!existingColumns.Contains(col.Name))
                {
                    string dataTypeString;
                    if (col.SQLiteDataType == null)
                        dataTypeString = "integer";
                    else
                        dataTypeString = col.SQLiteDataType;

                    string sql = "ALTER TABLE " + Name + " ADD COLUMN [" + col.Name + "] " + dataTypeString;
                    connection.ExecuteNonQuery(sql);
                }
            }
        }

        /// <summary>Convert .NET type into an SQLite type</summary>
        private string GetSQLiteDataType(object value)
        {
            // Convert the value we found above into an SQLite data type string and return it.
            Type type = null;
            if (value == null)
                return null;
            else
                type = value.GetType();

            if (type == null)
                return "integer";
            else if (type.ToString() == "System.DateTime")
                return "date";
            else if (type.ToString() == "System.Int32")
                return "integer";
            else if (type.ToString() == "System.Single")
                return "real";
            else if (type.ToString() == "System.Double")
                return "real";
            else
                return "char(50)";
        }

        /// <summary>Create a prepared insert query</summary>
        /// <param name="connection">The SQLite connection to write to</param>
        private IntPtr CreateInsertQuery(SQLite connection)
        {
            StringBuilder sql = new StringBuilder();
            sql.Append("INSERT INTO ");
            sql.Append(Name);
            sql.Append('(');

            for (int i = 0; i < Columns.Count; i++)
            {
                if (i > 0)
                    sql.Append(',');
                sql.Append('[');
                sql.Append(Columns[i].Name);
                sql.Append(']');
            }
            sql.Append(") VALUES (");

            for (int i = 0; i < Columns.Count; i++)
            {
                if (i > 0)
                    sql.Append(',');
                sql.Append('?');
            }
            sql.Append(')');
            return connection.Prepare(sql.ToString());
        }

        /// <summary>
        /// 'Flatten' the row passed in, into a list of columns ready to be added
        /// to a data table.
        /// </summary>
        public void Flatten(ref IEnumerable<string> columnNames, ref IEnumerable<string> columnUnits, ref IEnumerable<object> columnValues)
        {
            List<string> newColumnNames = new List<string>();
            List<string> newColumnUnits = new List<string>();
            List<object> newValues = new List<object>();

            for (int i = 0; i < columnValues.Count(); i++)
            {
                string units = null;
                if (columnUnits != null)
                    units = columnUnits.ElementAt(i);
                FlattenValue(columnNames.ElementAt(i),
                             units,
                             columnValues.ElementAt(i),
                             newColumnNames, newColumnUnits, newValues);
            }

            columnNames = newColumnNames;
            columnUnits = newColumnUnits;
            columnValues = newValues;
        }

        /// <summary>
        /// 'Flatten' a value (if it is an array or structure) into something that can be
        /// stored in a flat database table.
        /// </summary>
        /// <param name="name"></param>
        /// <param name="units"></param>
        /// <param name="value"></param>
        /// <param name="newColumnNames"></param>
        /// <param name="newColumnUnits"></param>
        /// <param name="newValues"></param>
        private static void FlattenValue(string name, string units, object value,
                                         List<string> newColumnNames, List<string> newColumnUnits, List<object> newValues)
        {
            if (value == null || value.GetType() == typeof(DateTime) || value.GetType() == typeof(string) || !value.GetType().IsClass)
            {
                // Scalar
                newColumnNames.Add(name);
                newColumnUnits.Add(units);
                newValues.Add(value);
            }
            else if (value.GetType().IsArray)
            {
                // Array
                Array array = value as Array;

                for (int columnIndex = 0; columnIndex < array.Length; columnIndex++)
                {
                    string heading = name;
                    heading += "(" + (columnIndex + 1).ToString() + ")";

                    object arrayElement = array.GetValue(columnIndex);
                    FlattenValue(heading, units, arrayElement,
                                 newColumnNames, newColumnUnits, newValues);  // recursion
                }
            }
            else if (value.GetType().GetInterface("IList") != null)
            {
                // List
                IList array = value as IList;
                for (int columnIndex = 0; columnIndex < array.Count; columnIndex++)
                {
                    string heading = name;
                    heading += "(" + (columnIndex + 1).ToString() + ")";

                    object arrayElement = array[columnIndex];
                    FlattenValue(heading, units, arrayElement,
                                 newColumnNames, newColumnUnits, newValues);  // recursion                }
                }
            }
            else
            {
                // A struct or class
                foreach (PropertyInfo property in ReflectionUtilities.GetPropertiesSorted(value.GetType(), BindingFlags.Instance | BindingFlags.Public))
                {
                    object[] attrs = property.GetCustomAttributes(true);
                    string propUnits = null;
                    bool ignore = false;
                    foreach (object attr in attrs)
                    {
                        if (attr is XmlIgnoreAttribute)
                        {
                            ignore = true;
                            continue;
                        }
                        Core.UnitsAttribute unitsAttr = attr as Core.UnitsAttribute;
                        if (unitsAttr != null)
                            propUnits = unitsAttr.ToString();
                    }
                    if (ignore)
                        continue;
                    string heading = name + "." + property.Name;
                    object classElement = property.GetValue(value, null);
                    FlattenValue(heading, propUnits, classElement,
                                 newColumnNames, newColumnUnits, newValues);  // recursion
                }
            }
        }

    }
}
