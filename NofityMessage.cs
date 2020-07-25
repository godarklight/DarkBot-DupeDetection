namespace DarkBot.DupeDetection
{
    public class NotifyMessage
    {
        public readonly ulong channelID;
        public readonly ulong originalMessageID;
        public readonly ulong repostMessageID;

        public NotifyMessage(ulong channelID, ulong originalMessageID, ulong repostMessageID)
        {
            this.channelID = channelID;
            this.originalMessageID = originalMessageID;
            this.repostMessageID = repostMessageID;
        }
    }
}