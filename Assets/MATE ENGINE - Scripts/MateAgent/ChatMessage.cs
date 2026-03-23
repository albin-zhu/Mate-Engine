using System;

namespace MateEngine.Agent
{
    [Serializable]
    public class ChatMessage
    {
        public string role;
        public string content;

        public ChatMessage() { }

        public ChatMessage(string role, string content)
        {
            this.role = role;
            this.content = content;
        }

        public static ChatMessage User(string content) => new ChatMessage("user", content);
        public static ChatMessage Assistant(string content) => new ChatMessage("assistant", content);
    }

    public class LLMResponse
    {
        public string content;
        public bool success;
        public string error;
    }
}
