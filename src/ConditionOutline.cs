using System.Text;
using System.Text.Json;
namespace SSEPreview;
internal static class ConditionOutline
{
    internal static string Render(JsonElement? configuration)
    {
        if(configuration is not {ValueKind:JsonValueKind.Object} root||!root.TryGetProperty("conditions",out var conditions))return "";
        var text=new StringBuilder("ACTIVE CONDITION OUTLINE (inspection only)\n");
        if(conditions.ValueKind!=JsonValueKind.Array)return text.AppendLine("Invalid conditions value; inspect the original configuration below.").ToString();
        text.AppendLine("ALL of the following:");
        if(conditions.GetArrayLength()==0)text.AppendLine("  (empty condition list)");
        foreach(var condition in conditions.EnumerateArray())Write(condition,1);
        text.AppendLine("Disabled conditions are shown but skipped by OAR. Actor state and custom condition plugins are not evaluated here.");
        return text.ToString();
        void Line(int depth,string value)=>text.Append(' ',depth*2).AppendLine(value);
        string Value(JsonElement value)=>value.ValueKind==JsonValueKind.String?JsonSerializer.Serialize(value.GetString(),new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}):value.GetRawText();
        void Write(JsonElement condition,int depth){
            if(depth>16){Line(depth,"(nested data omitted from outline; full source retained below)");return;}
            if(condition.ValueKind!=JsonValueKind.Object){Line(depth,"Invalid condition: "+Value(condition));return;}
            string name=condition.TryGetProperty("condition",out var n)&&n.ValueKind==JsonValueKind.String?n.GetString()!:"(missing condition name)";
            bool Flag(string key)=>condition.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
            string label=name switch{"OR"=>"ANY (OR)","AND"=>"ALL (AND)",_=>name};
            Line(depth,(Flag("disabled")?"[disabled] ":"")+(Flag("negated")?"NOT ":"")+label);
            foreach(var property in condition.EnumerateObject()){
                if(property.Name is "condition" or "disabled" or "negated")continue;
                if(property.Value.ValueKind==JsonValueKind.Array&&property.Name.Equals("Conditions",StringComparison.OrdinalIgnoreCase)){
                    Line(depth+1,property.Name+":");
                    if(property.Value.GetArrayLength()==0)Line(depth+2,"(empty list)");
                    foreach(var child in property.Value.EnumerateArray())Write(child,depth+2);
                }else if(property.Value.ValueKind==JsonValueKind.Object){
                    Line(depth+1,property.Name+":");foreach(var argument in property.Value.EnumerateObject())Line(depth+2,argument.Name+" = "+Value(argument.Value));
                }else Line(depth+1,property.Name+" = "+Value(property.Value));
            }
        }
    }
}
