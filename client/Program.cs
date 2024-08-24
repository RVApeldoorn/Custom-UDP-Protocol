using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MessageNS;

// SendTo();
class Program
{
    static void Main(string[] args)
    {
        ClientUDP cUDP = new ClientUDP();
        cUDP.start();
    }
}

class ClientUDP
{
    private Socket sock;
    private EndPoint ServerEndpoint;
    private List<MessageType> expectedMessageTypes;
    List<string> receivedIds = new List<string>();

    public ClientUDP()
    {
        expectedMessageTypes = new List<MessageType> { MessageType.Welcome, MessageType.Error };
        sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        IPAddress ipAddress = IPAddress.Parse("127.0.0.1"); // Server and client will only run locally
        ServerEndpoint = new IPEndPoint(ipAddress, 32000);
        File.WriteAllText("client_hamlet.txt", String.Empty);
    }

    public void start()
    {
        try
        {
            //Send Hello message to server
            SendHelloMessage();

            //Wait for response from server
            GetResponse();

            //Send RequestData message to server
            RequestData("hamlet.txt");

            //Enter loop to receive requested data
            while (true)
            {
                GetResponse();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nSocket Error: {ex.Message}. Terminating");
        }
        finally
        {
            //Close the socket!
            sock.Close();
        }
    }

    private void GetResponse()
    {
        byte[] buffer = new byte[1000];
        EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

        //Set long timeout for receiving messages from server
        sock.ReceiveTimeout = 5000;

        int receivedBytes = sock.ReceiveFrom(buffer, ref remoteEP);
        string data = Encoding.ASCII.GetString(buffer, 0, receivedBytes);

        //Print reveived message
        Console.WriteLine("\n\x1b[34m[Message]\x1b[0m Server said: " + data);

        //Process received message
        HandleMessage(data, ServerEndpoint);
    }

    private void HandleMessage(string receivedMessage, EndPoint ServerEndpoint)
    {
        try
        {
            //Deserialize the received message
            var data = JsonSerializer.Deserialize<Message>(receivedMessage);

            if (data == null || data.Content == null)
            {
                ErrorMessage(ServerEndpoint, "Invalid message format");
                EndProgram("Invalid message format. Terminating...");
                return;
            }

            //Check if type of received message is expected type
            if (!expectedMessageTypes.Contains(data.Type))
            {
                ErrorMessage(ServerEndpoint, $"unexpected Messagetype: {data.Type}");
                EndProgram("Invalid message format. Terminating...");
            }

            //Handle the message based on its type
            if (data.Type == MessageType.Welcome) //handle welcome msg
            {
                expectedMessageTypes.Add(MessageType.Data);
                expectedMessageTypes.Add(MessageType.End);
                expectedMessageTypes.Remove(MessageType.Welcome);
            }
            else if (data.Type == MessageType.Data) //handle data msg
            {
                HandleData(data);
            }
            else if (data.Type == MessageType.End) //handle end msg
            {
                EndProgram("\nServer ended connection. Terminating...");
            }
            else if (data.Type == MessageType.Error) //handle error msg
            {
                EndProgram("\nError received from server. Terminating...");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage(ServerEndpoint, "Error: " + ex.Message);
            EndProgram("Error: " + ex.Message); 
        }
    }

    private void HandleData(Message data)
    {
        if (data.Content != null && data.Content.Length >= 4)
        {
            //Get Id and data content from the received message
            string id = data.Content.Substring(0, 4);
            string msg = data.Content.Substring(4);

            //Check if the Id has already been received
            if (receivedIds.Contains(id))
            {
                ErrorMessage(ServerEndpoint, $"\nDuplicate message received with ID: {id}");
                EndProgram($"\nDuplicate message received with ID: {id}. Terminating");
                return;
            }

            //Write the data to the text file
            WriteToFile("client_hamlet.txt", msg);

            //Add the Id to the list of received IDs
            receivedIds.Add(id);

            //Send acknowledgment for the received data
            SendAck(id);
        }
    }

    private void EndProgram(string message)
    {
        Console.WriteLine(message);
        Environment.Exit(0);
    }

    private void SendAck(string id)
    {
        //Create an acknowledge message
        Message ackMessage = CreateMessage(MessageType.Ack, id);

        //Send the Acknowedge message
        SendMessages(ServerEndpoint, ackMessage);
    }

    private void SendHelloMessage()
    {
        //Create a Hello message
        Message helloObject = CreateMessage(MessageType.Hello, "20");

        //Send the Hello message
        SendMessages(ServerEndpoint, helloObject);
    }

    private void RequestData(string data)
    {
        //Create a request message
        Message requestdataobject = CreateMessage(MessageType.RequestData, data);

        //Send the request 
        SendMessages(ServerEndpoint, requestdataobject);
    }

    private void WriteToFile(string file, string data)
    {
        try
        {
            using (StreamWriter writer = new StreamWriter(file, true))
            {
                //Write data to the file
                writer.Write(data);
            }
            Console.WriteLine("\nData written to file successfully.");
        }
        catch (Exception ex)
        {
            ErrorMessage(ServerEndpoint, $"\nError writing to file: {ex.Message}");
            EndProgram($"\nError writing to file: {ex.Message}");
        }
    }

    private void ErrorMessage(EndPoint ServerEndpoint, string msg)
    {
        //Create an error message
        Message errorMessage = CreateMessage(MessageType.Error, msg);

        //Send the error message
        SendMessages(ServerEndpoint, errorMessage);
    }

    private void SendMessages(EndPoint receiver, Message message)
    {
        //Serialize the message to JSON
        string json = JsonSerializer.Serialize(message);

        //Convert the message to bytes
        byte[] BMessage = Encoding.ASCII.GetBytes(json);

        //Send the message to the client
        sock.SendTo(BMessage, BMessage.Length, SocketFlags.None, receiver);
    }

    private Message CreateMessage(MessageType type, string content)
    {
        //Create a message with given type and content
        Message message = new Message
        {
            Type = type,
            Content = content
        };

        return message;
    }
}