using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TodoEdge
{
    public class ProposedTask
    {
        public string Title { get; set; } = "";
        public DateTime? Due { get; set; }
        public string? Person { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>Sends a screenshot or text to the Claude Messages API and returns the to-do items it finds.</summary>
    public static class ClaudeService
    {
        private const string Endpoint = "https://api.anthropic.com/v1/messages";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

        private static string SystemPrompt(string? myName)
        {
            string today = DateTime.Today.ToString("dddd dd MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
            string who = string.IsNullOrWhiteSpace(myName)
                ? ""
                : $"\nThe reader is {myName.Trim()}. Treat that name, first name or initials as \"me\" when spotting their actions.";
            return
$@"You turn screenshots and text (emails, WhatsApp or Teams chats, documents, notes) into to-do items for the person who is reading them.
Today is {today}. The reader is the recipient of messages unless the content clearly shows otherwise.{who}

Rules:
- Only include actions the reader needs to do or follow up. Ignore things other people have promised to do, adverts, signatures and disclaimers.
- Each title starts with a verb, is under 12 words, uses British English, and names the person or subject, e.g. ""Reply to Jason about the pile schedule"" or ""Finalise the Q4 cost report"".
- Merge duplicates. Return at most 8 items, most important first.
- due: a date in yyyy-MM-dd only if a deadline is stated or clearly implied (resolve ""Friday"", ""tomorrow"", ""end of the week"" against today), otherwise null.
- person: the main person involved (first name or name as shown), otherwise null.
- notes: one short line of useful context (reference numbers, what is needed), otherwise null.
- If there is no action for the reader, return an empty list.

Respond with JSON only, no other text, in exactly this shape:
{{""tasks"":[{{""title"":""..."",""due"":""yyyy-MM-dd or null"",""person"":""... or null"",""notes"":""... or null""}}]}}";
        }

        public static async Task<List<ProposedTask>> ExtractAsync(string apiKey, string model, CaptureInput input, string? myName)
        {
            var content = new List<object>();
            string source = input.SourceName != null ? $" (\"{input.SourceName}\")" : "";
            const string minutesRule =
                " If it is meeting minutes or an action list, include actions owned by me and actions with no owner. " +
                "Include actions owned by others only if I clearly need to chase or approve them, and put the owner in person.";

            if (input.Png != null)
            {
                content.Add(new
                {
                    type = "image",
                    source = new { type = "base64", media_type = "image/png", data = Convert.ToBase64String(input.Png) }
                });
                content.Add(new { type = "text", text = "Extract my to-do items from this " + (input.Kind == "screenshot" ? "screenshot." : "image" + source + ".") + minutesRule });
            }
            else if (input.Pdf != null)
            {
                content.Add(new
                {
                    type = "document",
                    source = new { type = "base64", media_type = "application/pdf", data = Convert.ToBase64String(input.Pdf) }
                });
                content.Add(new { type = "text", text = "Extract my to-do items from this PDF" + source + "." + minutesRule });
            }
            else
            {
                string intro = input.Kind switch
                {
                    "dictation" => "This is my own note, dictated with speech-to-text, so it may contain recognition errors and filler words. " +
                                   "I am the person doing these tasks. Fix obvious misheard words and turn it into my to-do items:\n\n",
                    "email" => "Extract my to-do items from this email" + source + ". I am the recipient unless it clearly shows otherwise:\n\n",
                    "document" => "Extract my to-do items from this document" + source + "." + minutesRule + "\n\n",
                    _ => "Extract my to-do items from this text:\n\n"
                };
                content.Add(new { type = "text", text = intro + input.Text });
            }

            var body = new
            {
                model,
                max_tokens = 1500,
                system = SystemPrompt(myName),
                messages = new[] { new { role = "user", content } }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request);
            }
            catch (TaskCanceledException)
            {
                throw new Exception("Claude took too long to reply. Try again, or capture a smaller area.");
            }
            catch (HttpRequestException ex)
            {
                throw new Exception("Could not reach Claude. Check your internet connection.\n\n" + ex.Message);
            }

            using (response)
            {
                string json = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) throw new Exception(DescribeError(response.StatusCode, json));

                using var doc = JsonDocument.Parse(json);
                var sb = new StringBuilder();
                foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "text")
                        sb.Append(block.GetProperty("text").GetString());
                }
                return ParseTasks(sb.ToString());
            }
        }

        public static List<ProposedTask> ParseTasks(string reply)
        {
            var list = new List<ProposedTask>();
            int start = reply.IndexOf('{');
            int end = reply.LastIndexOf('}');
            if (start < 0 || end <= start) return list;

            using var doc = JsonDocument.Parse(reply.Substring(start, end - start + 1));
            if (!doc.RootElement.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var t in tasks.EnumerateArray())
            {
                string? title = Str(t, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;

                DateTime? due = null;
                string? dueText = Str(t, "due");
                if (dueText != null && DateTime.TryParseExact(dueText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d))
                    due = d;

                list.Add(new ProposedTask
                {
                    Title = title.Trim(),
                    Due = due,
                    Person = Str(t, "person"),
                    Notes = Str(t, "notes")
                });
            }
            return list;
        }

        private static string? Str(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
            string? s = v.GetString();
            if (string.IsNullOrWhiteSpace(s) || s.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
            return s.Trim();
        }

        private static string DescribeError(HttpStatusCode status, string json)
        {
            string detail = "";
            try
            {
                using var doc = JsonDocument.Parse(json);
                detail = doc.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "";
            }
            catch
            {
                detail = json.Length > 300 ? json.Substring(0, 300) : json;
            }

            return (int)status switch
            {
                401 => "Claude rejected the API key. Check it in Settings.",
                403 => "This API key is not allowed to use that model. " + detail,
                404 => "Model not found. Check the model name in Settings. " + detail,
                413 => "The screenshot is too large. Capture a smaller area.",
                429 => "Rate limit or credit limit reached. Wait a minute or check your Anthropic Console billing. " + detail,
                529 => "Claude is overloaded right now. Try again in a moment.",
                _ => $"Claude returned an error ({(int)status}). {detail}"
            };
        }
    }
}
