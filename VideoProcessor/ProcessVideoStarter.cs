using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;

namespace VideoProcessor
{
    public static class ProcessVideoStarter
    {
        [FunctionName("ProcessVideoStarter")]
        public static async Task<HttpResponseMessage> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]
            HttpRequestMessage req,
            [OrchestrationClient] DurableOrchestrationClient starter,
            TraceWriter log)
        {
            // parse query parameter
            string video = req.GetQueryNameValuePairs()
                .FirstOrDefault(q => string.Compare(q.Key, "video", true) == 0)
                .Value;

            // Get request body
            dynamic data = await req.Content.ReadAsAsync<object>();

            // Set name to query string or body data
            video = video ?? data?.video;

            if (video == null)
            {
                return req.CreateResponse(HttpStatusCode.BadRequest,
                   "Please pass the video location the query string or in the request body");
            }

            log.Info($"About to start orchestration for {video}");

            var orchestrationId = await starter.StartNewAsync("O_ProcessVideo", video);

            return starter.CreateCheckStatusResponse(req, orchestrationId);
        }

        [FunctionName("SubmitVideoApproval")]
        public static async Task<HttpResponseMessage> SubmitVideoApproval(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "SubmitVideoApproval/{id}")]
            HttpRequestMessage req, [OrchestrationClient] DurableOrchestrationClient client,
            [Table("Approvals", "Approval", "{id}", Connection = "AzureWebJobsStorage")] Approval approval,
            TraceWriter log)
        {
            // nb if the approval code doesn't exist, framework just returns a 404 before we get here
            string result = req.GetQueryNameValuePairs()
                .FirstOrDefault(q => string.Compare(q.Key, "result", true) == 0).Value;

            if (result == null)
                return req.CreateResponse(HttpStatusCode.BadRequest, "Need an approval result");

            log.Warning($"Sending approval result to {approval.OrchestrationId} of {result}");
            // send the ApprovalResult external event to this orchestration
            await client.RaiseEventAsync(approval.OrchestrationId, "ApprovalResult", result);

            return req.CreateResponse(HttpStatusCode.OK);
        }
    }
}
