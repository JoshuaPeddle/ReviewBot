namespace ReviewBot.Core.Llm;

public static class ReviewJsonSchema
{
    public static string Build(bool includeContextRequests)
    {
        var contextRequestsSchema = includeContextRequests
            ? """
              ,
                    "context_requests": {
                      "type": "array",
                      "items": {
                        "type": "object",
                        "properties": {
                          "path": { "type": "string" },
                          "reason": { "type": "string" }
                        },
                        "required": ["path"],
                        "additionalProperties": false
                      }
                    }
              """
            : string.Empty;

        return $$"""
               {
                 "type": "object",
                 "properties": {
                   "summary": { "type": "string" },
                   "comments": {
                     "type": "array",
                     "items": {
                       "type": "object",
                       "properties": {
                         "path": { "type": "string" },
                         "line": { "type": "integer" },
                         "severity": { "type": "string", "enum": ["info", "warning", "error"] },
                         "confidence": { "type": "string", "enum": ["high", "medium", "low"] },
                         "body": { "type": "string" }
                       },
                       "required": ["path", "line", "severity", "confidence", "body"],
                       "additionalProperties": false
                     }
                   }{{contextRequestsSchema}}
                 },
                 "required": ["summary", "comments"],
                 "additionalProperties": false
               }
               """;
    }
}
