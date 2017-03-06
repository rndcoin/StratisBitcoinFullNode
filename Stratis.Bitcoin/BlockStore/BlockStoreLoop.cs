﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using Stratis.Bitcoin.BlockPulling;
using Stratis.Bitcoin.Configuration;
using Stratis.Bitcoin.Connection;
using Stratis.Bitcoin.MemoryPool;
using Stratis.Bitcoin.Utilities;

namespace Stratis.Bitcoin.BlockStore
{
	public class BlockPair
	{
		public Block Block;
		public ChainedBlock ChainedBlock;
	}

	public class BlockStoreLoop
	{
		private readonly ConcurrentChain chain;
		private readonly ConnectionManager connection;
		public BlockRepository BlockRepository { get; } // public for testing
		private readonly DateTimeProvider dateTimeProvider;
		private readonly NodeArgs nodeArgs;
		private readonly BlockingPuller blockPuller;
		public BlockStore.ChainBehavior.ChainState ChainState { get; }

		public ConcurrentDictionary<uint256, BlockPair> PendingStorage { get; }

		public BlockStoreLoop(ConcurrentChain chain, ConnectionManager connection, BlockRepository blockRepository,
			DateTimeProvider dateTimeProvider, NodeArgs nodeArgs, BlockStore.ChainBehavior.ChainState chainState, 
			CancellationTokenSource globalCancellationTokenSource, BlockingPuller blockPuller)
		{
			this.chain = chain;
			this.connection = connection;
			this.BlockRepository = blockRepository;
			this.dateTimeProvider = dateTimeProvider;
			this.nodeArgs = nodeArgs;
			this.blockPuller = blockPuller;
			this.ChainState = chainState;
			
			PendingStorage = new ConcurrentDictionary<uint256, BlockPair>();
			StoredBlock = chain.GetBlock(this.BlockRepository.BlockHash);
			chainState.HighestPersistedBlock = this.StoredBlock;
			this.Loop(globalCancellationTokenSource.Token);
		}

		private int batchsize = 30;
		private TimeSpan pushInterval = TimeSpan.FromSeconds(10);
		private TimeSpan pushIntervalIBD = TimeSpan.FromMilliseconds(100);
		private DateTime lastpush;

		public void AddToPending(Block block)
		{
			var chainedBlock = this.chain.GetBlock(block.GetHash());

			if (chainedBlock == null)
			{
				// block not in main chain
				// TODO: check for reorg and delete blocks
				return ;
			}

			this.PendingStorage.TryAdd(chainedBlock.HashBlock, new BlockPair {Block = block, ChainedBlock = chainedBlock});
		}

		public async Task FlushPendingBlocks(CancellationToken token)
		{
			while (!token.IsCancellationRequested)
			{
				// if in IBD insert in batches	
				if (this.ChainState.IsInitialBlockDownload)
				{
					if (this.PendingStorage.Skip(0).Count() < batchsize) // ConcurrentDictionary perg
                        return;
				}
				else
				{
					// check the delay interval
					// while not in IBD blocks can still be validated in batches
					// putting a time interval will allow frequent interval blocks to accumulate
					if (lastpush + pushInterval > DateTime.UtcNow)
					{
						if (this.PendingStorage.Count < batchsize)
							return;
					}
				}

				lastpush = DateTime.UtcNow;
				if(!this.PendingStorage.Any())
					return;
				// take a batch of entries and push them to store
				var entries = this.PendingStorage.Select(s =>s .Value).Take(batchsize).ToList(); // ConcurrentDictionary perg
                await this.BlockRepository.PutAsync(entries.Select(b => b.Block).ToList(), false);
				BlockPair outvar;
				foreach (var entry in entries)
					this.PendingStorage.TryRemove(entry.ChainedBlock.HashBlock, out outvar);

				// this can be twicked if insert is effecting the consensus speed
				if (this.ChainState.IsInitialBlockDownload)
					await Task.Delay(pushIntervalIBD, token);
			}
		}

		public Task Flush()
		{
			// just try to write everything to disk
			return this.BlockRepository.PutAsync(this.PendingStorage.Select(b => b.Value.Block).ToList(), false);
		}

		public ChainedBlock StoredBlock { get; private set; }

		public void Loop(CancellationToken cancellationToken)
		{
            //// two loops: one for flushing the pending blocks to disk, 
            //// blocks received from the consensus loop validation.
            //// the other to keep track of stored blocks and download missing blocks

            AsyncLoop.Run("BlockStoreLoop.FlushPendingBlocks", async token =>
            {
                await FlushPendingBlocks(token);
            },
            cancellationToken, 
            repeateEvery: TimeSpans.Second,
            startAfter: TimeSpans.FiveSeconds);

            AsyncLoop.Run("BlockStoreLoop.DownloadBlocks", async token =>
			{
				await DownloadBlocks(cancellationToken);
            },
            cancellationToken,
            repeateEvery: TimeSpans.Second,
            startAfter: TimeSpans.FiveSeconds);
        }

        public async Task DownloadBlocks(CancellationToken token)
		{
		    while (!token.IsCancellationRequested)
		    {
		        if (StoredBlock.Height == this.ChainState.HighestValidatedPoW?.Height)
		            break;

                // find next block to download
                var next = this.chain.GetBlock(StoredBlock.Height + 1);
		        if (next == null)
		            break; //no blocks to store

		        if (await this.BlockRepository.ExistAsync(next.HashBlock))
		        {
		            // next block is in storage update StoredBlock 
		            await this.BlockRepository.SethBlockHash(next.HashBlock);
		            this.StoredBlock = next;
		            this.ChainState.HighestPersistedBlock = this.StoredBlock;
		            continue;
		        }

		        // check if the next block is pending storage
		        if (this.PendingStorage.ContainsKey(next.HashBlock))
		            break;

		        // block is not in store and not in pending 
		        // download the block or blocks
		        // find a batch of blocks to download
		        var todownload = new List<ChainedBlock>(new[] {next});
		        var bestfound = next;
		        foreach (var index in Enumerable.Range(1, batchsize - 1))
		        {
		            next = this.chain.GetBlock(next.Height + 1);

		            // stop if at the tip or block is already in store or pending insertion
		            if (next == null) break;
                    if (next.Height == this.ChainState.HighestValidatedPoW?.Height) break;
                    if (await this.BlockRepository.ExistAsync(next.HashBlock)) break;
		            if (this.PendingStorage.ContainsKey(next.HashBlock)) break;

		            todownload.Add(next);
		            bestfound = next;
		        }

		        // download and store missing blocks
		        var blocks = await this.blockPuller.AskBlocks(token, todownload.ToArray());
		        await this.BlockRepository.PutAsync(blocks, false);
		        // TODO: it mat be safer to update StoredBlock at the next loop instead of using bestfound
		        await this.BlockRepository.SethBlockHash(bestfound.HashBlock);
		        this.StoredBlock = bestfound;
		        this.ChainState.HighestPersistedBlock = this.StoredBlock;
		    }
		}
	}
}