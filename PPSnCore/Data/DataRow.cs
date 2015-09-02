﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using TecWare.DES.Stuff;

using static TecWare.PPSn.Data.PpsDataHelper;

namespace TecWare.PPSn.Data
{
	#region -- enum PpsDataRowState -----------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary>Status der Zeile.</summary>
	public enum PpsDataRowState
	{
		/// <summary>Invalid row state</summary>
		Unknown = -1,
		/// <summary>The row is unchanged</summary>
		Unchanged = 0,
		/// <summaryThe row is modified</summary>
		Modified = 1,
		/// <summary>The row is deleted</summary>
		Deleted = 2
	} // enum PpsDataRowState

	#endregion

	#region -- class PpsDataRow ---------------------------------------------------------

	///////////////////////////////////////////////////////////////////////////////
	/// <summary></summary>
	public class PpsDataRow : IDynamicMetaObjectProvider, INotifyPropertyChanged
	{
		#region -- class NotSetValue ------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary>Interne Klasse für Current-Value die anzeigt, ob sich ein Wert zum Original hin geändert hat.</summary>
		private sealed class NotSetValue
		{
			public override string ToString()
			{
				return "NotSet";
			} // func ToString
		} // class NotSetValue

		#endregion

		#region -- class PpsDataRowValueChangedItem ---------------------------------------

		private class PpsDataRowValueChangedItem : IPpsUndoItem
		{
			private PpsDataRow row;
			private int columnIndex;
			private object oldValue;
			private object newValue;

			public PpsDataRowValueChangedItem(PpsDataRow row, int columnIndex, object oldValue, object newValue)
			{
				this.row = row;
				this.columnIndex = columnIndex;
				this.oldValue = oldValue;
				this.newValue = newValue;
			} // ctor

			public override string ToString()
			{
				return String.Format("Undo ColumnChanged: {0}", columnIndex);
			} // func ToString

			private object GetOldValue()
			{
				return oldValue == NotSet ? row.originalValues[columnIndex] : oldValue;
			} // func GetOldValue

			public void Undo()
			{
				row.currentValues[columnIndex] = oldValue;
				row.OnValueChanged(columnIndex, newValue, GetOldValue());
			} // proc Undo

			public void Redo()
			{
				row.currentValues[columnIndex] = newValue;
				row.OnValueChanged(columnIndex, GetOldValue(), newValue);
			} // proc Redo
		} // class PpsDataRowValueChangedItem

		#endregion

		#region -- class PpsDataRowStateChangedItem ---------------------------------------

		private class PpsDataRowStateChangedItem : IPpsUndoItem
		{
			private PpsDataRow row;
			private PpsDataRowState oldValue;
			private PpsDataRowState newValue;

			public PpsDataRowStateChangedItem(PpsDataRow row, PpsDataRowState oldValue, PpsDataRowState newValue)
			{
				this.row = row;
				this.oldValue = oldValue;
				this.newValue = newValue;
			} // ctor

			public override string ToString()
			{
				return String.Format("Undo RowState: {0} -> {1}", oldValue, newValue);
			} // func ToString

			public void Undo()
			{
				row.RowState = oldValue;
			} // proc Undo

			public void Redo()
			{
				row.RowState = newValue;
			} // proc Redo
		} // class PpsDataRowStateChangedItem

		#endregion

		#region -- class PpsDataRowMetaObject ---------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private abstract class PpsDataRowBaseMetaObject : DynamicMetaObject
		{
			public PpsDataRowBaseMetaObject(Expression expression, object value)
				: base(expression, BindingRestrictions.Empty, value)
			{
			} // ctor

			private BindingRestrictions GetRestriction()
			{
				Expression expr;
				Expression exprType;
				if (ItemInfo.DeclaringType == typeof(PpsDataRow))
				{
					expr = Expression.Convert(Expression, typeof(PpsDataRow));
					exprType = Expression.TypeIs(Expression, typeof(PpsDataRow));
				}
				else
				{
					expr = Expression.Field(Expression.Convert(Expression, ItemInfo.DeclaringType), RowFieldInfo);
					exprType = Expression.TypeIs(Expression, typeof(RowValues));
				}

				expr =
					Expression.AndAlso(
						exprType,
						Expression.Equal(
							Expression.Property(Expression.Field(expr, TableFieldInfo), PpsDataTable.TableDefinitionPropertyInfo),
							Expression.Constant(Row.table.TableDefinition, typeof(PpsDataTableDefinition))
						)
					);

				return BindingRestrictions.GetExpressionRestriction(expr);
			} // func GetRestriction

			private Expression GetIndexExpression(int iColumnIndex)
			{
				return Expression.MakeIndex(
					Expression.Convert(Expression, ItemInfo.DeclaringType),
					ItemInfo,
					new Expression[] { Expression.Constant(iColumnIndex) }
				);
			} // func GetIndexExpression
			
			public override DynamicMetaObject BindGetMember(GetMemberBinder binder)
			{
				if (PpsDataHelper.IsStandardMember(LimitType, binder.Name))
					return base.BindGetMember(binder);

				// find a column
				var columnIndex = Row.table.TableDefinition.FindColumnIndex(binder.Name);
				if (columnIndex >= 0)
					return new DynamicMetaObject(GetIndexExpression(columnIndex), GetRestriction());
				else 
        {
					PpsDataTableRelationDefinition relation;
					if (ItemInfo.DeclaringType == typeof(PpsDataRow) && (relation = Row.table.TableDefinition.FindRelation(binder.Name)) != null)  // find a relation
					{
						return new DynamicMetaObject(
							Expression.Call(Expression.Convert(Expression, typeof(PpsDataRow)), CreateRelationMethodInfo, Expression.Constant(relation)),
							GetRestriction()
						);
					}
					else
						return new DynamicMetaObject(Expression.Constant(null, typeof(object)), GetRestriction());
				}
			} // func BindGetMember

			public override DynamicMetaObject BindSetMember(SetMemberBinder binder, DynamicMetaObject value)
			{
				if (PpsDataHelper.IsStandardMember(LimitType, binder.Name))
					return base.BindSetMember(binder, value);

				var columnIndex = Row.table.TableDefinition.FindColumnIndex(binder.Name);
				if (columnIndex >= 0)
				{
					return new DynamicMetaObject(
						Expression.Assign(GetIndexExpression(columnIndex), Expression.Convert(value.Expression, typeof(object))),
						GetRestriction().Merge(value.Restrictions)
					);
				}
				else
					return new DynamicMetaObject(Expression.Empty(), GetRestriction());
			} // func BindSetMember

			public override IEnumerable<string> GetDynamicMemberNames()
			{
				foreach (var col in Row.table.Columns)
					yield return col.Name;
			} // func GetDynamicMemberNames

			protected abstract PpsDataRow Row { get; }
			protected abstract PropertyInfo ItemInfo { get; }
		} // class PpsDataRowMetaObject

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class PpsDataRowMetaObject : PpsDataRowBaseMetaObject
		{
			public PpsDataRowMetaObject(Expression expression, object value)
				: base(expression, value)
			{
			} // ctor

			protected override PpsDataRow Row { get { return (PpsDataRow)Value; } }
			protected override PropertyInfo ItemInfo { get { return ItemPropertyInfo; } }
		} // class PpsDataRowMetaObject

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class PpsDataRowValuesMetaObject : PpsDataRowBaseMetaObject
		{
			public PpsDataRowValuesMetaObject(Expression expression, object value)
				: base(expression, value)
			{
			} // ctor

			protected override PpsDataRow Row { get { return ((RowValues)Value).Row; } }
			protected override PropertyInfo ItemInfo { get { return ValuesPropertyInfo; } }
		} // class PpsDataRowValuesMetaObject

		#endregion

		#region -- class RowValues --------------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		public abstract class RowValues : IDynamicMetaObjectProvider
		{
			private PpsDataRow row;

			#region -- Ctor/Dtor ------------------------------------------------------------

			protected RowValues(PpsDataRow row)
			{
				this.row = row;
			} // ctor

			/// <summary></summary>
			/// <param name="parameter"></param>
			/// <returns></returns>
			public DynamicMetaObject GetMetaObject(Expression parameter)
			{
				return new PpsDataRowValuesMetaObject(parameter, this);
			} // func GetMetaObject

			#endregion

			/// <summary>Ermöglicht den Zugriff auf die Spalte.</summary>
			/// <param name="columnIndex">Index der Spalte</param>
			/// <returns>Wert in der Spalte</returns>
			public abstract object this[int iColumnIndex] { get; set; }

			/// <summary>Ermöglicht den Zugriff auf die Spalte.</summary>
			/// <param name="columnName">Name der Spalte</param>
			/// <returns>Wert in der Spalte</returns>
			public object this[string sColumnName]
			{
				get { return this[Row.table.TableDefinition.FindColumnIndex(sColumnName, true)]; }
				set { this[Row.table.TableDefinition.FindColumnIndex(sColumnName, true)] = value; }
			} // prop this

			/// <summary>Zugriff auf die Datenzeile.</summary>
			protected internal PpsDataRow Row { get { return row; } }
		} // class RowValues

		#endregion

		#region -- class OriginalRowValues ------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class OriginalRowValues : RowValues
		{
			public OriginalRowValues(PpsDataRow row)
				: base(row)
			{
			} // ctor

			public override object this[int iColumnIndex]
			{
				get { return Row.originalValues[iColumnIndex]; }
				set { throw new NotSupportedException(); }
			} // prop this
		} // class OriginalRowValues

		#endregion

		#region -- class CurrentRowValues -------------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class CurrentRowValues : RowValues, INotifyPropertyChanged
		{
			public CurrentRowValues(PpsDataRow row)
				: base(row)
			{
			} // ctor

			public override object this[int columnIndex]
			{
				get
				{
					object currentValue = Row.currentValues[columnIndex];
					return currentValue == NotSet ? Row.originalValues[columnIndex] : currentValue;
				}
				set
				{
					// Convert the value to the expected type
					value = Procs.ChangeType(value, Row.table.TableDefinition.Columns[columnIndex].DataType);

					// Is the value changed
					object oldValue = this[columnIndex];
					if (!Object.Equals(oldValue, value))
						Row.SetCurrentValue(columnIndex, oldValue, value);
				}
			} // prop CurrentRowValues

			public event PropertyChangedEventHandler PropertyChanged
			{
				add { Row.PropertyChanged += value; }
				remove { Row.PropertyChanged -= value; }
			} // prop PropertyChanged
		} // class CurrentRowValues

		#endregion

		#region -- class PpsDataRelatedFilter ---------------------------------------------

		///////////////////////////////////////////////////////////////////////////////
		/// <summary></summary>
		private sealed class PpsDataRelatedFilter : PpsDataFilter
		{
			private PpsDataRow parentRow;
			private int parentColumnIndex;
			private int childColumnIndex;

			public PpsDataRelatedFilter(PpsDataRow parentRow, int parentColumnIndex, PpsDataTable childTable, int childColumnIndex)
				 : base(childTable)
			{
				if (parentRow == null)
					throw new ArgumentNullException();
				if (parentColumnIndex < 0 || parentColumnIndex >= parentRow.Table.Columns.Count)
					throw new ArgumentOutOfRangeException("parentColumnIndex");
				if (childColumnIndex < 0 || childColumnIndex >= childTable.Columns.Count)
					throw new ArgumentOutOfRangeException("childColumnIndex");

				this.parentRow = parentRow;
				this.parentColumnIndex = parentColumnIndex;
				this.childColumnIndex = childColumnIndex;

				Refresh();
			} // ctor

			public override PpsDataRow Add(params object[] values)
			{
				if (values == null || values.Length == 0)
					values = new object[Table.Columns.Count];

				values[childColumnIndex] = parentRow[parentColumnIndex];
				return base.Add(values);
			} // func Add
			
			protected override bool FilterRow(PpsDataRow row) => Object.Equals(parentRow[parentColumnIndex], row[childColumnIndex]);
		} // class PpsDataRelatedFilter

		#endregion

		internal static readonly object NotSet = new NotSetValue();

		/// <summary>Wird ausgelöst, wenn sich eine Eigenschaft geändert hat</summary>
		public event PropertyChangedEventHandler PropertyChanged;

		private PpsDataTable table;

		private PpsDataRowState rowState;
		private OriginalRowValues orignalValuesProxy;
		private CurrentRowValues currentValuesProxy;
		private object[] originalValues;
		private object[] currentValues;

		#region -- Ctor/Dtor --------------------------------------------------------------

		private PpsDataRow(PpsDataTable table)
		{
			this.rowState = PpsDataRowState.Unchanged;

			this.table = table;
			this.orignalValuesProxy = new OriginalRowValues(this);
			this.currentValuesProxy = new CurrentRowValues(this);

			// Create the empty arrays for the column values
			this.originalValues = new object[table.Columns.Count];
			this.currentValues = new object[originalValues.Length];
		} // ctor

		/// <summary>Creates a new empty row.</summary>
		/// <param name="table">Table that owns the row.</param>
		/// <param name="rowState">Initial state of the row.</param>
		/// <param name="originalValues">Defined original/default values for the row.</param>
		/// <param name="currentValues">Initial values for the row.</param>
		internal PpsDataRow(PpsDataTable table, PpsDataRowState rowState, object[] originalValues, object[] currentValues)
			: this(table)
		{
			this.rowState = rowState;

			int length = table.Columns.Count;

			if (originalValues == null || originalValues.Length != length)
				throw new ArgumentException("Nicht genug Werte für die Initialisierung.");
			if (currentValues == null)
				currentValues = new object[length];
			else if (currentValues.Length != length)
				throw new ArgumentException("Nicht genug Werte für die Initialisierung.");

			for (int i = 0; i < length; i++)
			{
				var typeTo = table.Columns[i].DataType;

				// set the originalValue
				var newOriginalValue = originalValues[i] == null ? null : Procs.ChangeType(originalValues[i], typeTo);
				table.Columns[i].OnColumnValueChanging(this, PpsDataColumnValueChangingFlag.Initial, null, ref newOriginalValue);

				// get the new value
				var newCurrentValue = currentValues[i] == null ? NotSet : (Procs.ChangeType(currentValues[i], typeTo) ?? NotSet);
				if (newCurrentValue != NotSet)
					table.Columns[i].OnColumnValueChanging(this, PpsDataColumnValueChangingFlag.SetValue, null, ref newCurrentValue);

				// set the values
				this.originalValues[i] = newOriginalValue;
				this.currentValues[i] = newCurrentValue;
			}
		} // ctor

		internal PpsDataRow(PpsDataTable table, XElement xRow)
			: this(table)
		{
			int rowState = xRow.GetAttribute(xnDataRowState, 0); // optional state of the row
			if (!Enum.IsDefined(typeof(PpsDataRowState), rowState))
				throw new ArgumentException($"Unexpected value '{rowState}' for <{xnDataRow.LocalName} @{xnDataRowState}>.");

			this.rowState = (PpsDataRowState)rowState;

			var i = 0;
			foreach (XElement xValue in xRow.Elements(xnDataRowValue)) // values
			{
				if (i >= table.Columns.Count)
					throw new ArgumentException("Mehr Datenwerte als Spaltendefinitionen gefunden");

				var xOriginal = xValue.Element(xnDataRowValueOriginal);
				var xCurrent = xValue.Element(xnDataRowValueCurrent);

				// load the values
				Type valueType = table.Columns[i].DataType;
				originalValues[i] = xOriginal == null || xOriginal.IsEmpty ? null : Procs.ChangeType(xOriginal.Value, valueType);
				currentValues[i] = xCurrent == null ? NotSet : xCurrent.IsEmpty ? null : Procs.ChangeType(xCurrent.Value, valueType);

				// notify
				var newValue = this[i];
				Table.Columns[i].OnColumnValueChanging(this, PpsDataColumnValueChangingFlag.Notify, null, ref newValue);

				i++;
			}

			if (i != originalValues.Length)
				throw new ArgumentOutOfRangeException("columns");
		} // ctor

		DynamicMetaObject IDynamicMetaObjectProvider.GetMetaObject(Expression parameter)
		{
			return new PpsDataRowMetaObject(parameter, this);
		} // func IDynamicMetaObjectProvider.GetMetaObject

		public override string ToString()
		{
			return $"PpsDataRow: {table.Name}";
    } // func ToString

		#endregion

		#region -- Commit, Reset, Remove --------------------------------------------------

		private void CheckForTable()
		{
			if (table == null)
				throw new InvalidOperationException();
		} // proc CheckForTable

		/// <summary>Removes all current values and restores the original loaded values. Supports Undo.</summary>
		public void Reset()
		{
			CheckForTable();

			// Reset all values
			var undo = GetUndoSink();
      using (var trans = undo?.BeginTransaction("Reset row"))
			{
				for (int i = 0; i < originalValues.Length; i++)
				{
					if (currentValues[i] != NotSet)
					{
						object oldValue = currentValues[i];
						currentValues[i] = NotSet;
						undo?.Append(new PpsDataRowValueChangedItem(this, i, oldValue, NotSet));
						OnValueChanged(i, oldValue, originalValues[i]);
					}
				}

				// this row was delete restore it
				if (rowState == PpsDataRowState.Deleted)
					table.RestoreInternal(this);

				undo?.Append(new PpsDataRowStateChangedItem(this, rowState, PpsDataRowState.Unchanged));
				RowState = PpsDataRowState.Unchanged;
				
				trans?.Commit();
			}
		} // proc Reset

		/// <summary>Commits the current values to the orignal values. Breaks Undo, you must clear the Undostack.</summary>
		public void Commit()
		{
			CheckForTable();

			if (rowState == PpsDataRowState.Deleted)
			{
				table.RemoveInternal(this, true);
			}
			else
			{
				for (int i = 0; i < originalValues.Length; i++)
				{
					if (currentValues[i] != NotSet)
					{
						originalValues[i] = currentValues[i];
						currentValues[i] = NotSet;
						
					}
				}

				RowState = PpsDataRowState.Unchanged;
			}
		} // proc Commit

		/// <summary>Marks the current row as deleted. Supprots Undo</summary>
		/// <returns><c>true</c>, wenn die Zeile als gelöscht markiert werden konnte.</returns>
		public bool Remove()
		{
			if (rowState == PpsDataRowState.Deleted || table == null)
				return false;

			var undo = GetUndoSink();
			using (var trans = undo?.BeginTransaction("Delete row."))
			{
				var r = table.RemoveInternal(this, false);

				undo?.Append(new PpsDataRowStateChangedItem(this, rowState, PpsDataRowState.Deleted));
				RowState = PpsDataRowState.Deleted;

				trans?.Commit();
				return r;
			}
		} // proc Remove

		#endregion

		#region -- Write ------------------------------------------------------------------

		/// <summary>Schreibt den Inhalt der Datenzeile</summary>
		/// <param name="row"></param>
		internal void Write(XmlWriter x)
		{
			// Status
			x.WriteAttributeString(xnDataRowState.LocalName, ((int)rowState).ToString());
			if (IsAdded)
				x.WriteAttributeString(xnDataRowAdd.LocalName, "1");

			// Werte
			for (int i = 0; i < originalValues.Length; i++)
			{
				x.WriteStartElement(xnDataRowValue.LocalName);

				if (!IsAdded && originalValues[i] != null)
					WriteValue(x, xnDataRowValueOriginal, originalValues[i]);
				if (rowState != PpsDataRowState.Deleted && currentValues[i] != NotSet)
					WriteValue(x, xnDataRowValueCurrent, currentValues[i]);

				x.WriteEndElement();
			}
		} // proc Write

		private void WriteValue(XmlWriter x, XName tag, object value)
		{
			x.WriteStartElement(tag.LocalName);
			if (value != null)
				x.WriteValue(Procs.ChangeType(value, typeof(string)));
			x.WriteEndElement();
		} // proc WriteValue

		#endregion

		#region -- Index Zugriff ----------------------------------------------------------

		private IPpsUndoSink GetUndoSink()
		{
			var sink = table.DataSet.UndoSink;
			return sink != null && !sink.InUndoRedoOperation ? sink : null;
		} // func GetUndoSink

		private void UpdateRelatedValues(int columnIndex, object oldValue, object value)
		{
			// change related values
			foreach (var r in table.TableDefinition.Relations)
			{
				if (r.ParentColumn.Table == table.TableDefinition && r.ParentColumn.Index == columnIndex)
				{
					var childTable = table.DataSet.FindTableFromDefinition(r.ChildColumn.Table);
					var childColumnIndex = r.ChildColumn.Index;
					for (int i = 0; i < childTable.Count; i++)
					{
						if (Object.Equals(childTable[i][childColumnIndex], oldValue))
							childTable[i][childColumnIndex] = value;
					}
				}
			}
		} // proc UpdateRelatedValues

		private void SetCurrentValue(int columnIndex, object oldValue, object value)
		{
			if (!table.Columns[columnIndex].OnColumnValueChanging(this, PpsDataColumnValueChangingFlag.SetValue, oldValue, ref value))
				return;

			var realCurrentValue = currentValues[columnIndex];
			currentValues[columnIndex] = value; // change the value

			// fill undo stack
			var undo = GetUndoSink();
			if (undo != null)
			{
				// calculate caption and define the transaction
				var column = table.Columns[columnIndex];
				using (var trans = undo.BeginTransaction(String.Format(">{0}< geändert.", column.Meta.Get(PpsDataColumnMetaData.Caption, column.Name))))
				{
					UpdateRelatedValues(columnIndex, oldValue, value);
					undo.Append(new PpsDataRowValueChangedItem(this, columnIndex, realCurrentValue, value));
					OnValueChanged(columnIndex, oldValue, value); // Notify the value change 
					trans.Commit();
				}
			}
			else
			{
				UpdateRelatedValues(columnIndex, oldValue, value);
				OnValueChanged(columnIndex, oldValue, value); // Notify the value change 
			}
		} // proc SetCurrentValue

		/// <summary>If the value of the row gets changed, this method is called.</summary>
		/// <param name="columnIndex"></param>
		/// <param name="oldValue"></param>
		/// <param name="value"></param>
		protected virtual void OnValueChanged(int columnIndex, object oldValue, object value)
		{
			if (RowState == PpsDataRowState.Unchanged)
			{
				GetUndoSink()?.Append(new PpsDataRowStateChangedItem(this, PpsDataRowState.Unchanged, PpsDataRowState.Modified));
				RowState = PpsDataRowState.Modified;
			}

			table.OnColumnValueChanged(this, columnIndex, oldValue, value);
			OnPropertyChanged(table.Columns[columnIndex].Name);
		} // proc OnValueChanged

		protected virtual void OnPropertyChanged(string sPropertyName)
		{
			if (PropertyChanged != null)
				PropertyChanged(this, new PropertyChangedEventArgs(sPropertyName));
		} // proc OnPropertyChanged

		/// <summary>Zugriff auf den aktuellen Wert.</summary>
		/// <param name="columnIndex">Spalte</param>
		/// <returns></returns>
		public object this[int columnIndex]
		{
			get { return currentValuesProxy[columnIndex]; }
			set { currentValuesProxy[columnIndex] = value; }
		} // prop this

		/// <summary>Zugriff auf den aktuellen Wert.</summary>
		/// <param name="columnName">Spalte</param>
		/// <returns></returns>
		public object this[string columnName]
		{
			get { return currentValuesProxy[columnName]; }
			set { currentValuesProxy[columnName] = value; }
		} // prop this

		/// <summary>Zugriff auf die aktuellen Werte</summary>
		public RowValues Current { get { return currentValuesProxy; } }
		/// <summary>Originale Werte, mit der diese Zeile initialisiert wurde</summary>
		public RowValues Original { get { return orignalValuesProxy; } }

		/// <summary>Status der Zeile</summary>
		public PpsDataRowState RowState
		{
			get { return rowState; }
			private set
			{
				if (rowState != value)
				{
					rowState = value;
					OnPropertyChanged("RowState");
				}
			}
		} // prop RowState

		/// <summary>Wurde die Datenzeile neu angefügt.</summary>
		public bool IsAdded { get { return table == null ? false : table.OriginalRows.Contains(this); } }

		#endregion

		#region -- CreateRelation -----------------------------------------------------------

		public PpsDataFilter CreateRelation(PpsDataTableRelationDefinition relation)
		{
			return new PpsDataRelatedFilter(this, relation.ParentColumn.Index, table.DataSet.FindTableFromDefinition(relation.ChildColumn.Table), relation.ChildColumn.Index);
		} // func CreateRelation

		#endregion

		/// <summary>Zugehörige Datentabelle</summary>
		public PpsDataTable Table { get { return table; } internal set { table = value; } }

		// -- Static --------------------------------------------------------------

		private static readonly PropertyInfo RowStatePropertyInfo;
		private static readonly PropertyInfo ItemPropertyInfo;
		private static readonly PropertyInfo CurrentPropertyInfo;
		private static readonly PropertyInfo OriginalPropertyInfo;
		private static readonly FieldInfo TableFieldInfo;
		private static readonly MethodInfo ResetMethodInfo;
		private static readonly MethodInfo CommitMethodInfo;
		private static readonly MethodInfo CreateRelationMethodInfo;

		private static readonly PropertyInfo ValuesPropertyInfo;
		private static readonly FieldInfo RowFieldInfo;

		#region -- sctor ------------------------------------------------------------------

		static PpsDataRow()
		{
			var typeRowInfo = typeof(PpsDataRow).GetTypeInfo();
			RowStatePropertyInfo = typeRowInfo.GetDeclaredProperty(nameof(RowState));
			ItemPropertyInfo = FindItemIndex(typeRowInfo);
			CurrentPropertyInfo = typeRowInfo.GetDeclaredProperty(nameof(Current));
			OriginalPropertyInfo = typeRowInfo.GetDeclaredProperty(nameof(Original));
			TableFieldInfo = typeRowInfo.GetDeclaredField(nameof(table));
			ResetMethodInfo = typeRowInfo.GetDeclaredMethod(nameof(Reset));
			CommitMethodInfo = typeRowInfo.GetDeclaredMethod(nameof(Commit));
			CreateRelationMethodInfo = typeRowInfo.GetDeclaredMethod(nameof(CreateRelation));

			var typeValueInfo = typeof(RowValues).GetTypeInfo();
			ValuesPropertyInfo = FindItemIndex(typeValueInfo);
			RowFieldInfo = typeValueInfo.GetDeclaredField("row");

			if (RowStatePropertyInfo == null ||
					ItemPropertyInfo == null ||
					CurrentPropertyInfo == null ||
					OriginalPropertyInfo == null ||
					TableFieldInfo == null ||
					ResetMethodInfo == null ||
					CommitMethodInfo == null ||
					CreateRelationMethodInfo == null ||
          ValuesPropertyInfo == null ||
					RowFieldInfo == null)
				throw new InvalidOperationException("Reflection fehlgeschlagen (PpsDataRow)");
		} // sctor

		private static PropertyInfo FindItemIndex(TypeInfo typeInfo)
		{
			return (from pi in typeInfo.DeclaredProperties where pi.Name == "Item" && pi.GetIndexParameters()[0].ParameterType == typeof(int) select pi).FirstOrDefault();
		} // func FindItemIndex

		#endregion
	} // class PpsDataRow

	#endregion
}
