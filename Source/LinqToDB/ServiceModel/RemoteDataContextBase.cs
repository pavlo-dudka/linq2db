﻿#if NETFRAMEWORK
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;

using JetBrains.Annotations;

namespace LinqToDB.ServiceModel
{
	using Expressions;
	using Extensions;
	using LinqToDB.Common;
	using Mapping;
	using SqlProvider;

	[PublicAPI]
	public abstract partial class RemoteDataContextBase : IDataContext
	{
		public string? Configuration { get; set; }

		class ConfigurationInfo
		{
			public LinqServiceInfo LinqServiceInfo = null!;
			public MappingSchema   MappingSchema   = null!;
		}

		static readonly ConcurrentDictionary<string,ConfigurationInfo> _configurations = new ();

		class RemoteMappingSchema : MappingSchema
		{
			public RemoteMappingSchema(string configuration, MappingSchema mappingSchema)
				: base(configuration, mappingSchema)
			{
			}
		}

		ConfigurationInfo? _configurationInfo;

		ConfigurationInfo GetConfigurationInfo()
		{
			if (_configurationInfo == null && !_configurations.TryGetValue(Configuration ?? "", out _configurationInfo))
			{
				var client = GetClient();

				try
				{
					var info = client.GetInfo(Configuration);

					var type = Type.GetType(info.MappingSchemaType)!;
					var ms   = new RemoteMappingSchema(ContextIDPrefix, (MappingSchema)Activator.CreateInstance(type));

					_configurationInfo = new ConfigurationInfo
					{
						LinqServiceInfo = info,
						MappingSchema   = ms,
					};
				}
				finally
				{
					(client as IDisposable)?.Dispose();
				}
			}

			return _configurationInfo;
		}

		protected abstract ILinqClient  GetClient();
		protected abstract IDataContext Clone    ();
		protected abstract string       ContextIDPrefix { get; }

		string?            _contextID;
		string IDataContext.ContextID => _contextID ??= GetConfigurationInfo().MappingSchema.ConfigurationList[0];

		private MappingSchema? _mappingSchema;
		public  MappingSchema   MappingSchema
		{
			get => _mappingSchema ??= GetConfigurationInfo().MappingSchema;
			set
			{
				_mappingSchema = value;
				_serializationMappingSchema = new SerializationMappingSchema(_mappingSchema);
			}
		}

		private  MappingSchema? _serializationMappingSchema;
		internal MappingSchema  SerializationMappingSchema => _serializationMappingSchema ??= new SerializationMappingSchema(MappingSchema);

		public  bool InlineParameters { get; set; }
		public  bool CloseAfterUse    { get; set; }


		private List<string>? _queryHints;
		public  List<string>   QueryHints => _queryHints ??= new List<string>();

		private List<string>? _nextQueryHints;
		public  List<string>   NextQueryHints => _nextQueryHints ??= new List<string>();

		private        Type? _sqlProviderType;
		public virtual Type   SqlProviderType
		{
			get
			{
				if (_sqlProviderType == null)
				{
					var type = GetConfigurationInfo().LinqServiceInfo.SqlBuilderType;
					_sqlProviderType = Type.GetType(type)!;
				}

				return _sqlProviderType;
			}

			set => _sqlProviderType = value;
		}

		private        Type? _sqlOptimizerType;
		public virtual Type   SqlOptimizerType
		{
			get
			{
				if (_sqlOptimizerType == null)
				{
					var type = GetConfigurationInfo().LinqServiceInfo.SqlOptimizerType;
					_sqlOptimizerType = Type.GetType(type)!;
				}

				return _sqlOptimizerType;
			}

			set => _sqlOptimizerType = value;
		}

		SqlProviderFlags IDataContext.SqlProviderFlags      => GetConfigurationInfo().LinqServiceInfo.SqlProviderFlags;
		TableOptions     IDataContext.SupportedTableOptions => GetConfigurationInfo().LinqServiceInfo.SupportedTableOptions;

		Type IDataContext.DataReaderType => typeof(ServiceModelDataReader);

		Expression IDataContext.GetReaderExpression(IDataReader reader, int idx, Expression readerExpression, Type toType)
		{
			var dataType   = reader.GetFieldType(idx);
			var methodInfo = GetReaderMethodInfo(dataType);

			Expression ex = Expression.Call(readerExpression, methodInfo, ExpressionInstances.Int32Array(idx));

			if (ex.Type != dataType)
				ex = Expression.Convert(ex, dataType);

			return ex;
		}

		static MethodInfo GetReaderMethodInfo(Type type)
		{
			switch (type.ToNullableUnderlying().GetTypeCodeEx())
			{
				case TypeCode.Boolean  : return MemberHelper.MethodOf<IDataReader>(r => r.GetBoolean (0));
				case TypeCode.Byte     : return MemberHelper.MethodOf<IDataReader>(r => r.GetByte    (0));
				case TypeCode.Char     : return MemberHelper.MethodOf<IDataReader>(r => r.GetChar    (0));
				case TypeCode.Int16    : return MemberHelper.MethodOf<IDataReader>(r => r.GetInt16   (0));
				case TypeCode.Int32    : return MemberHelper.MethodOf<IDataReader>(r => r.GetInt32   (0));
				case TypeCode.Int64    : return MemberHelper.MethodOf<IDataReader>(r => r.GetInt64   (0));
				case TypeCode.Single   : return MemberHelper.MethodOf<IDataReader>(r => r.GetFloat   (0));
				case TypeCode.Double   : return MemberHelper.MethodOf<IDataReader>(r => r.GetDouble  (0));
				case TypeCode.String   : return MemberHelper.MethodOf<IDataReader>(r => r.GetString  (0));
				case TypeCode.Decimal  : return MemberHelper.MethodOf<IDataReader>(r => r.GetDecimal (0));
				case TypeCode.DateTime : return MemberHelper.MethodOf<IDataReader>(r => r.GetDateTime(0));
			}

			if (type == typeof(Guid))
				return MemberHelper.MethodOf<IDataReader>(r => r.GetGuid(0));

			return MemberHelper.MethodOf<IDataReader>(dr => dr.GetValue(0));
		}

		bool? IDataContext.IsDBNullAllowed(IDataReader reader, int idx)
		{
			return null;
		}

		static readonly Dictionary<Tuple<Type, SqlProviderFlags>, Func<ISqlBuilder>> _sqlBuilders = new ();

		Func<ISqlBuilder>? _createSqlProvider;

		Func<ISqlBuilder> IDataContext.CreateSqlProvider
		{
			get
			{
				if (_createSqlProvider == null)
				{
					var type = SqlProviderType;
					var key  = Tuple.Create(type, ((IDataContext)this).SqlProviderFlags);

					if (!_sqlBuilders.TryGetValue(key, out _createSqlProvider))
						lock (_sqlProviderType!)
							if (!_sqlBuilders.TryGetValue(key, out _createSqlProvider))
								_sqlBuilders.Add(key, _createSqlProvider =
									Expression.Lambda<Func<ISqlBuilder>>(
										Expression.New(
											type.GetConstructor(new[]
											{
												typeof(MappingSchema),
												typeof(ISqlOptimizer),
												typeof(SqlProviderFlags)
											}),
											new Expression[]
											{
												Expression.Constant(((IDataContext)this).MappingSchema),
												Expression.Constant(GetSqlOptimizer()),
												Expression.Constant(((IDataContext)this).SqlProviderFlags)
											})).CompileExpression());
				}

				return _createSqlProvider;
			}
		}

		static readonly Dictionary<Tuple<Type, SqlProviderFlags>, Func<ISqlOptimizer>> _sqlOptimizers = new ();

		Func<ISqlOptimizer>? _getSqlOptimizer;

		public Func<ISqlOptimizer> GetSqlOptimizer
		{
			get
			{
				if (_getSqlOptimizer == null)
				{
					var type = SqlOptimizerType;
					var key  = Tuple.Create(type, ((IDataContext)this).SqlProviderFlags);

					if (!_sqlOptimizers.TryGetValue(key, out _getSqlOptimizer))
						lock (_sqlOptimizerType!)
							if (!_sqlOptimizers.TryGetValue(key, out _getSqlOptimizer))
								_sqlOptimizers.Add(key, _getSqlOptimizer =
									Expression.Lambda<Func<ISqlOptimizer>>(
										Expression.New(
											type.GetConstructor(new[]
											{
												typeof(SqlProviderFlags)
											}),
											new Expression[]
											{
												Expression.Constant(((IDataContext)this).SqlProviderFlags)
											})).CompileExpression());
				}

				return _getSqlOptimizer;
			}
		}

		List<string>? _queryBatch;
		int           _batchCounter;

		public void BeginBatch()
		{
			_batchCounter++;

			if (_queryBatch == null)
				_queryBatch = new List<string>();
		}

		public void CommitBatch()
		{
			if (_batchCounter == 0)
				throw new InvalidOperationException();

			_batchCounter--;

			if (_batchCounter == 0)
			{
				var client = GetClient();

				try
				{
					var data = LinqServiceSerializer.Serialize(SerializationMappingSchema, _queryBatch!.ToArray());
					client.ExecuteBatch(Configuration, data);
				}
				finally
				{
					(client as IDisposable)?.Dispose();
					_queryBatch = null;
				}
			}
		}

		public async Task CommitBatchAsync()
		{
			if (_batchCounter == 0)
				throw new InvalidOperationException();

			_batchCounter--;

			if (_batchCounter == 0)
			{
				var client = GetClient();

				try
				{
					var data = LinqServiceSerializer.Serialize(SerializationMappingSchema, _queryBatch!.ToArray());
					await client.ExecuteBatchAsync(Configuration, data).ConfigureAwait(Common.Configuration.ContinueOnCapturedContext);
				}
				finally
				{
					(client as IDisposable)?.Dispose();
					_queryBatch = null;
				}
			}
		}

		IDataContext IDataContext.Clone(bool forNestedQuery)
		{
			ThrowOnDisposed();

			return Clone();
		}

		public event EventHandler? OnClosing;

		/// <inheritdoc/>
		public Action<EntityCreatedEventArgs>? OnEntityCreated { get; set; }

		protected bool Disposed { get; private set; }

		protected void ThrowOnDisposed()
		{
			if (Disposed)
				throw new ObjectDisposedException("RemoteDataContext", "IDataContext is disposed, see https://github.com/linq2db/linq2db/wiki/Managing-data-connection");
		}

		void IDataContext.Close()
		{
			Close();
		}

		Task IDataContext.CloseAsync()
		{
			Close();
			return TaskEx.CompletedTask;
		}

		void Close()
		{
			OnClosing?.Invoke(this, EventArgs.Empty);
		}

		public void Dispose()
		{
			Disposed = true;

			Close();
		}

#if !NATIVE_ASYNC
		public Task DisposeAsync()
		{
			Disposed = true;

			return ((IDataContext)this).CloseAsync();
		}
#else
		public ValueTask DisposeAsync()
		{
			Disposed = true;

			return new ValueTask(((IDataContext)this).CloseAsync());
		}
#endif

	}
}
#endif
