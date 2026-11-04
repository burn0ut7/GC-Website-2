using System.Text.Json;
using Npgsql;
namespace GC.Web;

public static class AdminDatabase
{
    // Explicitly reviewed application tables. Never expose arbitrary SQL or auth/config secrets.
    private static readonly Dictionary<string,string> Tables = new()
    {
        ["users"]="id", ["external_identities"]="provider,subject", ["account_preferences"]="user_id",
        ["worker_heartbeat"]="name", ["ingest_receipts"]="server_id,stream,recording_id,revision",
        ["telemetry_batches"]="server_id,recording_id,revision", ["mission_batches"]="server_id,mission_id,stream,revision",
        ["mission_players"]="server_id,mission_id,identity", ["session_events"]="server_id,mission_id,sequence",
        ["delivery_failures"]="server_id,stream,filename"
    };
    public static void Map(RouteGroupBuilder routes)
    {
        routes.MapGet("/tables", () => Results.Ok(Tables.Keys));
        routes.MapGet("/tables/{table}", async Task<IResult> (HttpContext context,string table,int? offset) =>
        {
            if (!Tables.TryGetValue(table,out var order)) return Results.NotFound();
            var skip=offset??0;if(skip<0||skip>1000000)return Results.BadRequest();
            var database=context.RequestServices.GetService<NpgsqlDataSource>();if(database is null)return Results.StatusCode(503);
            await using var query=database.CreateCommand($"SELECT left(row_to_json(t)::text,16384),length(row_to_json(t)::text)>16384 FROM gc.{table} t ORDER BY {order} LIMIT 51 OFFSET $1");
            query.CommandTimeout=10;query.Parameters.AddWithValue(skip);
            var rows=new List<object>();bool more=false;
            await using var reader=await query.ExecuteReaderAsync(context.RequestAborted);
            while(await reader.ReadAsync(context.RequestAborted)){if(rows.Count==50){more=true;break;}rows.Add(new{data=reader.GetString(0),truncated=reader.GetBoolean(1)});}
            return Results.Ok(new{rows,offset=skip,nextOffset=more?(int?)(skip+50):null});
        });
        routes.MapGet("/accounts", async Task<IResult> (HttpContext context,int? offset) =>
        {
            var skip=offset??0;if(skip<0||skip>1000000)return Results.BadRequest();
            var database=context.RequestServices.GetService<NpgsqlDataSource>();if(database is null)return Results.StatusCode(503);
            await using var query=database.CreateCommand("""
                SELECT jsonb_build_object('id',u.id,'createdAt',u.created_at,
                    'identities',COALESCE((SELECT jsonb_agg(to_jsonb(i)-'user_id') FROM gc.external_identities i WHERE i.user_id=u.id),'[]'::jsonb),
                    'preferences',(SELECT to_jsonb(p)-'user_id' FROM gc.account_preferences p WHERE p.user_id=u.id))::text
                FROM gc.users u ORDER BY u.created_at DESC,u.id LIMIT 51 OFFSET $1
                """);
            query.CommandTimeout=10;query.Parameters.AddWithValue(skip);
            var accounts=new List<JsonElement>();bool more=false;
            await using var reader=await query.ExecuteReaderAsync(context.RequestAborted);
            while(await reader.ReadAsync(context.RequestAborted)){if(accounts.Count==50){more=true;break;}using var json=JsonDocument.Parse(reader.GetString(0));accounts.Add(json.RootElement.Clone());}
            return Results.Ok(new{accounts,offset=skip,nextOffset=more?(int?)(skip+50):null});
        });
    }
}
