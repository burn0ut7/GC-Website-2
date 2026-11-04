using System.Text.Json;
using Npgsql;
namespace GC.Infrastructure;

public static class MissionDetails
{
    // Recover details from immutable validated snapshots; never add revisions together.
    public static async Task Backfill(NpgsqlDataSource db,string archive,CancellationToken ct)
    {
        var items=new List<(Guid Server,Guid Mission,int Revision,string File,int Bytes,string Hash)>();
        await using(var q=db.CreateCommand("""
            SELECT DISTINCT p.server_id,p.mission_id,p.revision,r.archive_name,r.byte_count,r.sha256
            FROM gc.mission_players p JOIN gc.ingest_receipts r ON r.server_id=p.server_id AND r.recording_id=p.mission_id
                AND r.stream='stats' AND r.revision=p.revision AND r.status='projected'
            WHERE p.details IS NULL LIMIT 8
            """))
        {await using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))items.Add((r.GetGuid(0),r.GetGuid(1),r.GetInt32(2),r.GetString(3),r.GetInt32(4),r.GetString(5)));}
        foreach(var item in items)
        {
            var bytes=await ArchiveFiles.ReadVerified(archive,item.File,item.Bytes,item.Hash,LobbyBatch.MaximumBytes("stats"),ct);
            using var batch=LobbyBatch.Read(bytes);
            if(batch.Stream!="stats"||batch.Id!=item.Mission||batch.Revision!=item.Revision)throw new InvalidDataException("Statistics archive mismatch");
            await using var connection=await db.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
            foreach(var p in batch.Root.GetProperty("players").EnumerateArray())
            {
                var details=JsonSerializer.Serialize(p.EnumerateObject().Where(v=>v.Name is "roles" or "assignments").ToDictionary(v=>v.Name,v=>v.Value));
                await using var q=new NpgsqlCommand("UPDATE gc.mission_players SET details=$5::jsonb WHERE server_id=$1 AND mission_id=$2 AND revision=$3 AND identity=$4 AND details IS NULL",connection,tx);
                q.Parameters.AddWithValue(item.Server);q.Parameters.AddWithValue(item.Mission);q.Parameters.AddWithValue(item.Revision);q.Parameters.AddWithValue(p.GetProperty("identity").GetString()!);q.Parameters.AddWithValue(details);await q.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }
}
