﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.Remoting;

using DBConnection;
using Timers;
using Configuration;
using Devices;

namespace Host
{
	public class CollectorHost : IDisposable
    {
		IDBConnection db;
		Thread dbThread;
		Queue<IQuery> dbQueue;
		List<IDevice> devices;
		Thread devicesThread;
		ITimer timer;
		EventWaitHandle timerSignal;
		EventWaitHandle queriesReadySignal;
		IConfigurator config;
		bool isInitialized;
		bool isRunning;
		bool isDisposing;

		/// <summary>
		/// Initializes a new instance of the <see cref="Host.CollectorHost"/> class.
		/// </summary>
		/// <param name="conf">Configuration.</param>
		public CollectorHost (IConfigurator conf)
		{
			config = conf;
			isInitialized = false;
			isRunning = false;
			isDisposing = false;
		}

		public void Init ()
		{
			if (isInitialized)
				return;
			try {
				OnInitializing ();

				// настраиваем БД
				var dbConf = config.GetItem ("db_conf") as DBConfiguration;
				if (dbConf == null)
					throw new Exception ("'db_conf' item coulnd't be found in the configuration");

				var dbFactory = Activator.CreateInstance (GetType (dbConf.Assembly, dbConf.Namespace, dbConf.FactoryType)) as IDBFactory;
				dbFactory.InitDBLayer (dbConf);
				db = dbFactory.CreateConnection ();
				dbThread = new Thread (new ThreadStart (HandleDBRequest));
				// и очередь БД
				var capacity = config.GetItem ("queue_capacity");
				if (capacity == null)
					throw new Exception ("queue_capacity item couldn't be found in the configuration");
				dbQueue = new Queue<IQuery> ((int)capacity);

				// список устройств
				var devConfList = config.GetItem ("dev_conf_list") as DeviceConfiguration[];
				if (devConfList == null)
					throw new Exception ("'dev_conf_list' couldn't be found in the configuration");
				devices = new List<IDevice> (devConfList.Length);
				foreach (DeviceConfiguration devConf in devConfList) {
					var dev = Activator.CreateInstance (GetType (devConf.Assembly, devConf.Namespace, devConf.DeviceType)) as IDevice;
					if (dev == null)
						throw new Exception ("Could not create device with id " + devConf.ID);
					var adapter = Activator.CreateInstance (GetType (devConf.Assembly, devConf.Namespace, dbFactory.GetAdapterTypeName (devConf.ID))) as IStorageAdapter;
					if (adapter == null)
						throw new Exception ("Adapter for device with id '" + dev.ID + "' could not be found!");
					dev.Init (devConf.ID, adapter);
					devices.Add (dev);
				}
				devicesThread = new Thread (new ThreadStart (CollectDeviceInfo));

				// настройки таймера
				// TODO: add mechanism for changing timers
				var timeInterval = config.GetItem ("timer_time_interval_ms");
				if (timeInterval == null)
					throw new Exception ("'timer_time_interval_ms' couldn't be found in the configuration object");
				timer = new TimeIntervalTimer ((int)timeInterval);

				// событие для таймера
				timerSignal = new ManualResetEvent (false);

				// событие для БД
				queriesReadySignal = new ManualResetEvent (false);

				isInitialized = true;

				OnInitialized ();

			} catch (Exception ex) {
				OnOutputPending ("Error occured: " + ex.ToString ());
				isInitialized = false;
			}
		}

		/// <summary>
		/// Возвращает System.Type из стринг для библиотеки, неймспейс, и имя типа
		/// </summary>
		/// <returns>The type.</returns>
		/// <param name="assembly">Assembly.</param>
		/// <param name="nmspace">Nmspace.</param>
		/// <param name="type">Type.</param>
		Type GetType (string assembly, string nmspace, string type)
		{
			return Type.GetType (String.Format ("{0}.{1}, {2}", nmspace, type, assembly));
		}


		/// <summary>
		/// Ждет запросы к БД в отдельном потоке
		/// </summary>
		void HandleDBRequest ()
		{
			if (db == null)
				throw new Exception ("DB configuration or connection object not available");

			// связываемся с БД
			db.Connect ();
			OnOutputPending ("Successfully connected to DB"); 

			int count;
			IQuery queryBuf;

			// бесконечный цикл обработки запросов
			for (;;) {
				// ждем пока очередь не заполнится других потоков
				queriesReadySignal.WaitOne ();

				try {

					// выполняем все запросы в очереди
					do {
						queryBuf = null;
						// вытаскиваем первого запроса из очереди
						lock (dbQueue) {
							if (dbQueue.Count > 0)
								queryBuf = dbQueue.Dequeue ();
						}

						// выполняем запроса
						// предполагаем, что это будет медлено выполнятся
						if (queryBuf != null) {
							if (db.ExecuteQuery (queryBuf)) {
								// query прошло, дать потребителю знать 
								OnOutputPending ("Executed query #0x"+queryBuf.GetHashCode ().ToString ("X"));
							} else {
								// если запрос не выполнился, покажи сообщение из СУБД
								OnOutputPending (db.GetLastResponse ());
							}
						}

						// так как прошло много времени с момента выполнения запроса
						// блокируем доступ к dbQueu и проверяем оставшихся элементов в очереди
						lock (dbQueue) {
							count = dbQueue.Count;
						}
					} while (count > 0);

				} catch (Exception ex) {
					OnOutputPending (ex.Message);
				}

				// выключаем сигнал пока не добавится элементов в очереди
				queriesReadySignal.Reset ();
			}
		}

		/// <summary>
		/// Собирает информацию с устройств
		/// </summary>
		void CollectDeviceInfo ()
		{
			object buf;
			IQuery queryBuf;

			for (;;) {
				// ждем следущего сигнала
				timerSignal.WaitOne ();

				try {
					// собираем инфу из устройств
					foreach (IDevice dev in devices) {
						// уведомляем потребителю
						OnOutputPending ("Collecting data from " + dev.ID); 
						// собиреам и проверяем
						buf = dev.GetData ();
						if (buf != null) {
							// подготовливаем для БД
							queryBuf = dev.Adapter.PrepareQuery (buf);
							// добавляем в очереди запросов
							lock (dbQueue) {
								dbQueue.Enqueue (queryBuf);
							}
							// уведомить поток БД что есть запрос на выполнение
							queriesReadySignal.Set ();
						}
					}

				} catch (Exception ex) {
					OnOutputPending (ex.Message);
				}

				// выключаем сигнал
				timerSignal.Reset ();
			}
		}

		/// <summary>
		/// Начинает снятия информации с устройств и запис в БД
		/// </summary>
		public void Start ()
		{
			if ( ! isInitialized || isRunning)
				return;

			try {
				OnStarting ();

				OnOutputPending ("Starting devices...");
				timer.Init (timerSignal);
				timer.Start ();
				devicesThread.Start ();
				dbThread.Start ();
				isRunning = true;

				OnStarted ();

			} catch (Exception ex) {
				OnOutputPending ("Error occured while trying to start data collection: " + ex.Message);
				OnOutputPending ("Terminating session.");
				Stop ();
			}
		}

		/// <summary>
		/// Перекращает работу потоков
		/// </summary>
		public void Stop ()
		{
			if (! isInitialized)
				return;
			try {
				OnStopping ();

				OnOutputPending ("Stopping host...");
				timer.Stop ();
				devicesThread.Abort ();
				devicesThread.Join ();
				OnOutputPending ("Devices stopped");
				dbThread.Abort ();
				dbThread.Join ();
				db.Disconnect ();
				OnOutputPending ("Database communication stopped");

				OnStopped ();
			
			} catch (Exception ex) {
				OnOutputPending ("Error occured while trying to stop data collection: " + ex.Message);
				OnOutputPending ("Terminating session.");
			} finally {
				// re-init on next run
				isInitialized = false;
				isRunning = false;
			}
		}

		public bool IsRunning {
			get { return isRunning; }
		}

		#region Events

		/// <summary>
		/// Происходит когда есть информация для вывода
		/// </summary>
		public event OutputPendingDelegate OutputPending;
		public event EventHandler<EventArgs> Initializing;
		public event EventHandler<EventArgs> Initialized;
		public event EventHandler<EventArgs> Starting;
		public event EventHandler<EventArgs> Started;
		public event EventHandler<EventArgs> Stopping;
		public event EventHandler<EventArgs> Stopped;

		public delegate void OutputPendingDelegate (string displayMe);

		void OnOutputPending (string msg) {
			if (OutputPending != null)
				OutputPending (msg);
		}

		void OnInitializing () {
			if (Initializing != null)
				Initializing (this, EventArgs.Empty);
		}

		void OnInitialized () {
			if (Initialized != null)
				Initialized (this, EventArgs.Empty);
		}

		void OnStarting () {
			if (Starting != null)
				Starting (this, EventArgs.Empty);
		}

		void OnStarted () {
			if (Started != null)
				Started (this, EventArgs.Empty);
		}

		void OnStopping () {
			if (Stopping != null)
				Stopping (this, EventArgs.Empty);
		}

		void OnStopped () {
			if (Stopped != null)
				Stopped (this, EventArgs.Empty);
		}

		#endregion Events

		#region IDisposable implementation

		public void Dispose ()
		{
			// если объекты поля хоста не инициализированы нет смысла их удалять
			if (! isInitialized)
				return;

			// если метод уже вызвали хотя бы 1 раз
			if (isDisposing)
				return;

			// если хост еще работает
			if (isRunning)
				Stop ();

			// даем все уборщику
			db.Dispose ();
			queriesReadySignal.Dispose ();
			timerSignal.Dispose ();
			isDisposing = true;
		}

		#endregion

    }
}
