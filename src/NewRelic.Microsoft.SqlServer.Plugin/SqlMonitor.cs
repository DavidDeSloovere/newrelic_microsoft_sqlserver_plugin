﻿using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NewRelic.Microsoft.SqlServer.Plugin.Communication;
using NewRelic.Microsoft.SqlServer.Plugin.Configuration;
using NewRelic.Microsoft.SqlServer.Plugin.Core;
using NewRelic.Microsoft.SqlServer.Plugin.Core.Extensions;
using NewRelic.Platform.Binding.DotNET;
using log4net;

namespace NewRelic.Microsoft.SqlServer.Plugin
{
	/// <summary>
	///     Polls SQL databases and reports the data back to a collector.
	/// </summary>
	internal class SqlMonitor
	{
		private static readonly ILog _VerboseSqlOutputLogger = LogManager.GetLogger(Constants.VerboseSqlLogger);
		private readonly AgentData _agentData;
		private readonly ILog _log;
		private readonly Settings _settings;
		private readonly object _syncRoot;
		private PollingThread _pollingThread;

		public SqlMonitor(Settings settings, ILog log = null)
		{
			_settings = settings;
			_syncRoot = new object();
			_log = log ?? LogManager.GetLogger(Constants.SqlMonitorLogger);

			// TODO Get AssemblyInfoVersion
			_agentData = new AgentData {Host = Environment.MachineName, Pid = Process.GetCurrentProcess().Id, Version = "1.0.0",};
		}

		public void Start()
		{
			try
			{
				lock (_syncRoot)
				{
					if (_pollingThread != null)
					{
						return;
					}

					_log.Info("----------------");
					_log.Info("Service Starting");

					var queries = new QueryLocator(new DapperWrapper()).PrepareQueries();

					var pollingThreadSettings = new PollingThreadSettings
					                            {
						                            Name = "SQL Monitor Query Polling Thread",
						                            InitialPollDelaySeconds = 0,
						                            PollIntervalSeconds = _settings.PollIntervalSeconds,
						                            PollAction = () => QueryServers(queries),
						                            AutoResetEvent = new AutoResetEvent(false),
					                            };

					_pollingThread = new PollingThread(pollingThreadSettings, _log);
					_pollingThread.ExceptionThrown += e => _log.Error("Polling thread exception", e);

					_log.Debug("Service Threads Starting...");

					_pollingThread.Start();

					_log.Debug("Service Threads Started");
				}
			}
			catch (Exception e)
			{
				_log.Fatal("Failed while attempting to start service");
				_log.Warn(e);
				throw;
			}
		}

		/// <summary>
		///     Performs the queries against the database
		/// </summary>
		/// <param name="queries"></param>
		private void QueryServers(IEnumerable<SqlMonitorQuery> queries)
		{
			try
			{
				var tasks = _settings.SqlServers
				                     .Select(server => Task.Factory.StartNew(() => QueryServer(queries, server, _log))
				                                           .Catch(e => _log.Debug(e))
				                                           .ContinueWith(t => t.Result.ForEach(ctx => ctx.Results.ForEach(r => ctx.Query.AddMetrics(ctx))))
				                                           .Catch(e => _log.Error(e))
				                                           .ContinueWith(t =>
				                                                         {
					                                                         var queryContexts = t.Result.ToArray();
					                                                         SendComponentDataToCollector(queryContexts);
					                                                         return queryContexts.Sum(q => q.MetricsRecorded);
				                                                         }))
				                     .ToArray();

				Task.WaitAll(tasks.ToArray<Task>());

				_log.InfoFormat("Recorded {0} metrics", tasks.Sum(t => t.Result));
			}
			catch (Exception e)
			{
				_log.Error(e);
			}
		}

		private static IEnumerable<QueryContext> QueryServer(IEnumerable<SqlMonitorQuery> queries, SqlServerToMonitor server, ILog log)
		{
			// Remove password from logging
			var safeConnectionString = new SqlConnectionStringBuilder(server.ConnectionString);
			if (!string.IsNullOrEmpty(safeConnectionString.Password))
			{
				safeConnectionString.Password = "[redacted]";
			}

			_VerboseSqlOutputLogger.InfoFormat("Connecting with {0}", safeConnectionString);
			_VerboseSqlOutputLogger.Info("");

			using (var conn = new SqlConnection(server.ConnectionString))
			{
				foreach (var query in queries)
				{
					object[] results;
					try
					{
						_VerboseSqlOutputLogger.InfoFormat("Executing {0}", query.ResourceName);
						results = query.Query(conn).ToArray();
						foreach (var result in results)
						{
							_VerboseSqlOutputLogger.Info(result.ToString());
						}
						_VerboseSqlOutputLogger.Info("");
					}
					catch (Exception e)
					{
						log.Error(string.Format("Error with query '{0}'", query.QueryName), e);
						continue;
					}
					yield return new QueryContext {Query = query, Results = results, ComponentData = new ComponentData(server.Name, Constants.ComponentGuid, 1),};
				}
			}
		}

		/// <summary>
		/// Sends data to New Relic, unless in "collect only" mode.
		/// </summary>
		/// <param name="queryContexts">Query data containing <see cref="ComponentData"/> where metrics are recorded</param>
		private void SendComponentDataToCollector(QueryContext[] queryContexts)
		{
			// Allows a testing mode that does not send data to New Relic
			if (_settings.CollectOnly)
			{
				return;
			}

			try
			{
				var platformData = new PlatformData(_agentData);
				queryContexts.ForEach(c => platformData.AddComponent(c.ComponentData));
				new SqlRequest(_settings.LicenseKey) {Data = platformData}.SendData();
			}
			catch (Exception e)
			{
				_log.Error("Error sending data to connector", e);
			}
		}

		public void Stop()
		{
			lock (_syncRoot)
			{
				if (_pollingThread == null)
				{
					return;
				}

				try
				{
					if (!_pollingThread.Running)
					{
						return;
					}

					_log.Debug("Service Threads Stopping...");
					_pollingThread.Stop(true);
					_log.Debug("Service Threads Stopped");
				}
				finally
				{
					_pollingThread = null;
					_log.Info("Service Stopped");
				}
			}
		}
	}
}
