﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Gridsum.DataflowEx.Database
{
    /// <summary>
    /// The class helps you to bulk insert parsed objects to multiple database tables (e.g. group by profileId)
    /// </summary>
    /// <typeparam name="T">The db-mapped type of parsed objects (usually generated by EF/linq2sql)</typeparam>
    public class MultiDbBulkInserter<T> : BlockContainerBase<T> where T:class
    {
        private BufferBlock<T> m_bufferBlock;
        private Func<T, int> m_dispatchFunc;
        private ConcurrentDictionary<int, Lazy<DbBulkInserter<T>>> m_bulkInserterMap;
        private Func<int, string> m_connectionGetter;
        private string m_destTable;
        private BlockContainerOptions m_options;
        private int m_bulkSize;
        private Task m_dispatchTask;
        private Func<int, Lazy<DbBulkInserter<T>>> m_initer;

        public MultiDbBulkInserter(BlockContainerOptions options, Func<T, int> dispatchFunc, Func<int, string> connectionGetter, string destTable, string destLabel, int bulkSize = 4096 * 2, string dbBulkInserterName = null)
            : base(options)
        {
            m_options = options;
            m_bulkInserterMap = new ConcurrentDictionary<int, Lazy<DbBulkInserter<T>>>();
            m_dispatchFunc = dispatchFunc;
            m_connectionGetter = connectionGetter;
            m_destTable = destTable;
            m_bulkSize = bulkSize;

            m_bufferBlock = new BufferBlock<T>(new ExecutionDataflowBlockOptions()
            {
                BoundedCapacity = m_containerOptions.RecommendedCapacity ?? -1,                
            });

            m_dispatchTask = DispatchLoop();

            m_initer = p => new Lazy<DbBulkInserter<T>>(
                        () => new DbBulkInserter<T>(m_connectionGetter(p), m_destTable, m_options, destLabel, m_bulkSize, string.Format("{0}_{1}", this.Name, p))
                    );

            RegisterBlock(m_bufferBlock, () => m_bufferBlock.Count, t => 
            {
                if (t.IsFaulted || t.IsCanceled)
                {
                    foreach (var kvPair in m_bulkInserterMap)
                    {
                        DbBulkInserter<T> singleProfileInserter = kvPair.Value.Value;
                        singleProfileInserter.Fault(t.Exception);
                    }
                }
                else
                {
                    foreach (var kvPair in m_bulkInserterMap)
                    {
                        DbBulkInserter<T> singleProfileInserter = kvPair.Value.Value;
                        singleProfileInserter.InputBlock.Complete();
                    }
                }
            });
        }

        protected async Task DispatchLoop()
        {
            await Task.Yield(); //ctor returns immediately

            bool awaited = false;
            T item;

            while (m_bufferBlock.TryReceive(out item) || (awaited = await m_bufferBlock.OutputAvailableAsync()) )
            {
                if (awaited)
                {
                    item = m_bufferBlock.Receive();
                }

                int profileId = m_dispatchFunc(item);
                var bulkInserter = m_bulkInserterMap.GetOrAdd(profileId, m_initer).Value;
                bulkInserter.InputBlock.Post(item);

                awaited = false;
            }
        }

        public override System.Threading.Tasks.Dataflow.ITargetBlock<T> InputBlock
        {
            get { return m_bufferBlock; }
        }

        protected override async Task GetCompletionTask()
        {
            await m_bufferBlock.Completion;
            await m_dispatchTask;
            
            await Task.WhenAll(m_bulkInserterMap.Select(kv => kv.Value.Value.CompletionTask));
            this.CleanUp();
        }
    }
}
