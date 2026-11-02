using Npgsql;

namespace GC.Infrastructure;

/// <summary>Same-host delivery. Only the server service reads the server profile.</summary>
public static class OutboxDelivery
{
    public static readonly IReadOnlyDictionary<string,string> Folders=new Dictionary<string,string>
    { ["stats"]="Lobby2Stats",["session"]="Lobby2SessionLogs",["history"]="Lobby2History",["telemetry"]="Lobby2Telemetry" };

    public static async Task<int> Deliver(NpgsqlDataSource database,string profile,string archive,Guid server,CancellationToken token)
    {
        if(!Path.IsPathFullyQualified(profile)||!Path.IsPathFullyQualified(archive)||server==Guid.Empty)
            throw new ArgumentException("Delivery requires absolute paths and a server identity.");
        RejectLink(profile);
        var identityPath=Path.Combine(profile,"Lobby2History","server-id.txt");
        RejectLink(Path.GetDirectoryName(identityPath)!);RejectLink(identityPath);
        if(!Guid.TryParse((await File.ReadAllTextAsync(identityPath,token)).Trim(),out var identity)||identity!=server)
            throw new InvalidDataException("Profile server identity does not match delivery configuration.");
        var delivered=0;
        foreach(var source in Folders)
        {
            token.ThrowIfCancellationRequested();
            var directory=Path.Combine(profile,source.Value);
            if(!Directory.Exists(directory))continue;
            RejectLink(directory);
            FileStream ownership;
            try{ownership=new FileStream(Path.Combine(directory,".ingest.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException){continue;} // The existing importer uses the same ownership lock.
            await using(ownership)
            {
                var deferred=new HashSet<string>(StringComparer.Ordinal);
                await using(var failures=database.CreateCommand("SELECT filename FROM gc.delivery_failures WHERE server_id=$1 AND stream=$2 AND next_attempt_at>now()"))
                {
                    failures.Parameters.AddWithValue(server);failures.Parameters.AddWithValue(source.Key);
                    await using var reader=await failures.ExecuteReaderAsync(token);
                    while(await reader.ReadAsync(token))deferred.Add(reader.GetString(0));
                }
                var attempted=0;
                foreach(var marker in Directory.EnumerateFiles(directory,"*.ready"))
                {
                    var filename=Path.GetFileName(marker)[..^6];
                    if(!(filename.EndsWith(".json",StringComparison.Ordinal)||filename.EndsWith(".l2t",StringComparison.Ordinal))||deferred.Contains(filename))continue;
                    if(attempted++>=8)break; // Per-stream fairness; telemetry cannot starve statistics/history.
                    token.ThrowIfCancellationRequested();
                    var path=Path.Combine(directory,filename);
                    try
                    {
                        RejectLink(marker);RejectLink(path);
                        if(new FileInfo(marker).Length!=5||await File.ReadAllTextAsync(marker,token)!="ready")
                            throw new InvalidDataException("Incomplete publication marker.");
                        byte[] bytes;
                        await using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                        {
                            if(file.Length<=0||file.Length>LobbyBatch.MaximumBytes(source.Key))throw new InvalidDataException("Invalid source size.");
                            bytes=new byte[(int)file.Length];await file.ReadExactlyAsync(bytes,token);
                        }
                        using var batch=LobbyBatch.Read(bytes);
                        if(batch.Stream!=source.Key||batch.FileName!=filename||
                            (batch.Stream=="history"&&batch.Root.GetProperty("serverId").GetGuid()!=server))
                            throw new InvalidDataException("Outbox identity mismatch.");
                        if(!await Intake.Receive(database,archive,server,batch.Stream,batch.Id,batch.Revision,bytes,token))
                        {await Failure(database,server,source.Key,filename,"revision_conflict",token);continue;}
                        // Durable archive + receipt precede acknowledgement. Restart replays safely.
                        // Unlike the old importer, source payloads are retained for all four streams.
                        await using var clear=database.CreateCommand("DELETE FROM gc.delivery_failures WHERE server_id=$1 AND stream=$2 AND filename=$3");
                        clear.Parameters.AddWithValue(server);clear.Parameters.AddWithValue(source.Key);clear.Parameters.AddWithValue(filename);
                        await clear.ExecuteNonQueryAsync(token);File.Delete(marker);delivered++;
                    }
                    catch(Exception error)when(LobbyBatch.IsInvalid(error)||error is IOException or UnauthorizedAccessException)
                    {
                        await Failure(database,server,source.Key,filename,LobbyBatch.IsInvalid(error)?"invalid_source":"source_unavailable",token);
                    }
                }
            }
        }
        return delivered;
    }

    private static async Task Failure(NpgsqlDataSource database,Guid server,string stream,string filename,string error,CancellationToken token)
    {
        await using var query=database.CreateCommand("""
            INSERT INTO gc.delivery_failures(server_id,stream,filename,attempts,error_code,next_attempt_at)
            VALUES($1,$2,$3,1,$4,now()+interval '30 seconds')
            ON CONFLICT(server_id,stream,filename) DO UPDATE SET attempts=gc.delivery_failures.attempts+1,
                error_code=excluded.error_code,next_attempt_at=now()+interval '30 seconds'*least(60,gc.delivery_failures.attempts+1)
            """);
        query.Parameters.AddWithValue(server);query.Parameters.AddWithValue(stream);query.Parameters.AddWithValue(filename);query.Parameters.AddWithValue(error);
        await query.ExecuteNonQueryAsync(token);
    }

    private static void RejectLink(string path)
    {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Outbox links are not supported.");
    }
}
