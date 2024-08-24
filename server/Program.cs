using System;
using System.Data.SqlTypes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MessageNS;


// Do not modify this class
class Program
{
    static void Main(string[] args)
    {
        ServerUDP sUDP = new ServerUDP();
        sUDP.start();
    }
}

class ServerUDP
{
    private Socket sock;
    private EndPoint? currentClientEndpoint; //Session identifier
    private List<MessageType> expectedMessageTypes;
    private int threshold = 20;
    private int windowSize = 1;
    private List<Message> sentChunks = new List<Message>(); //Sent Data messages in one sending iteration
    List<string> unackedIds = new List<string>(); //Ids of unacknowledged data messages in one sending iteration

    private bool sendingData = false;

    public ServerUDP()
    {
        try
        {
            currentClientEndpoint = null;  // Initialized to null until a client session is started (check for null value in HandleClient)
            expectedMessageTypes = new List<MessageType> { MessageType.Hello, MessageType.Error };
            sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            IPAddress ipAddress = IPAddress.Parse("127.0.0.1"); //server and client will only run locally
            IPEndPoint localEndpoint = new IPEndPoint(ipAddress, 32000);
            sock.Bind(localEndpoint);
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Error when creating Server: {ex.Message}");
            Environment.Exit(1); // Terminate the program when initalization of server fails
        }
    }

    public void start()
    {
        try
        {
            while (true)
            {
                Console.WriteLine("\nServer is waiting...");
                HandleClient();
            }
        }
        catch
        {
            Console.WriteLine("\nSocket Error. Terminating");
        }
        finally
        {
            //Close the socket!
            sock.Close();
        }
    }

    private void HandleClient()
    {
        try
        {
            // Set the receive timeout
            sock.ReceiveTimeout = 5000;

            byte[] buffer = new byte[1000];
            EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            //Receive messsage from anyone
            int receivedBytes = sock.ReceiveFrom(buffer, ref remoteEP);

            //Check if the message is from the current client session
            if (currentClientEndpoint != null && !remoteEP.Equals(currentClientEndpoint))
            {
                Console.WriteLine("\n\x1b[35m[Message]\x1b[0m Message received from a different client. Ignoring...");
                ErrorMessage(remoteEP, "The server is in a session with another client. Try again later.");
                return;
            }

            //Print the received message
            string data = Encoding.ASCII.GetString(buffer, 0, receivedBytes);
            Console.WriteLine("\n\x1b[35m[Message]\x1b[0m Message received from " + remoteEP.ToString() + " " + data);

            //Process the received message
            HandleMessage(data, remoteEP);
        }
        catch (SocketException ex)
        {
            if (ex.SocketErrorCode == SocketError.TimedOut)
            {
                Console.WriteLine("\nTimeout occurred while waiting for a message.");
                if (currentClientEndpoint != null){
                    ErrorMessage(currentClientEndpoint, "\nTimeout occurred while waiting for a message.");
                    EndSession(currentClientEndpoint, false);
                }
            }
            else
            {
                Console.WriteLine($"Socket error: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Socket error: {ex.Message}");
        }
    }

    private void HandleMessage(string message, EndPoint remoteEP)
    {
        try
        {
            //Deserialize the received message
            var receivedMessage = JsonSerializer.Deserialize<Message>(message);

            if (receivedMessage == null || receivedMessage.Content == null)
            {
                Console.WriteLine("\nInvalid message format. Ignoring...");
                ErrorMessage(remoteEP, "Invalid message format. Ignoring...");
                return;
            }

            //Check if recieved message is of expected type
            if (!expectedMessageTypes.Contains(receivedMessage.Type))
            {
                Console.WriteLine("\nInvalid message type.");
                ErrorMessage(remoteEP, $"unexpected Messagetype: {receivedMessage.Type}");
                EndSession(remoteEP, false);
                return;
            }

            //Handle the message based on its type
            if (receivedMessage.Type == MessageType.Hello) //handle hello msg
            {
                StartSession(remoteEP);
                SetThreshold(remoteEP, receivedMessage.Content);

                //Set expected MessageTypes to receive
                expectedMessageTypes.Add(MessageType.RequestData);
                expectedMessageTypes.Remove(MessageType.Hello);
            }
            else if (receivedMessage.Type == MessageType.RequestData) //handle requestdata msg
            {
                if (GetData(receivedMessage.Content, remoteEP))
                {
                    expectedMessageTypes.Add(MessageType.Ack);
                    expectedMessageTypes.Remove(MessageType.RequestData);
                    SendData(receivedMessage.Content, remoteEP);
                }
                else
                {
                    ErrorMessage(remoteEP, "Data not found");
                    EndSession(remoteEP, false);
                }
            }
            else if (receivedMessage.Type == MessageType.Error) //handle error msg
            {
                Console.WriteLine("\nError received from client. Restarting...");
                EndSession(remoteEP, false);
            }
            else if (receivedMessage.Type == MessageType.Ack) //handle ack msg
            {
                HandleAck(receivedMessage.Content);
            }
            else
            {
                Console.WriteLine("\nUnknown message type. Restarting...");
                ErrorMessage(remoteEP, "Unknown message type.");
                EndSession(remoteEP, false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
            ErrorMessage(remoteEP, ex.Message);
            EndSession(remoteEP, false);
        }
    }

    private void StartSession(EndPoint remoteEP)
    {
        //Start a new session with the client
        currentClientEndpoint = remoteEP;
        Console.WriteLine("\nSession started with client: " + currentClientEndpoint.ToString());

        //Create a Welcome message
        Message welcomeMessage = CreateMessage(MessageType.Welcome, "");

        // Send the Welcome message
        SendMessages(remoteEP, welcomeMessage);
    }

    private void SendData(string data, EndPoint currentClientEndpoint)
    {
        try
        {
            Console.WriteLine($"\nSending {data} to {currentClientEndpoint}");
            FileStream fileStream = File.OpenRead(data);
            StreamReader reader = new StreamReader(fileStream);
            char[] buffer = new char[100];
            int bytesRead;
            int id = 0; //From 0000 to 9999 = 10.000 messages before duplication
            bool endOfFile = false; //Flag for end of file
            bool missedacks = false; //Flag to (not) double windowSize
            sendingData = true;
            windowSize = 1;
            sentChunks.Clear();
            unackedIds.Clear();
            //Read and send the file contents in chunks
            while (!endOfFile && sendingData)
            {
                if (windowSize > threshold)
                {
                    Console.WriteLine("\nThreshold exceeded. Adjusting window size.");
                    windowSize = threshold; // Adjust the window size to the threshold (if uneven threshold)
                }

                //Send chunks based on the window size
                for (int i = 0; i < windowSize; i++)
                {
                    //Read next chunk of text from file
                    bytesRead = reader.Read(buffer, 0, buffer.Length);

                    //Exit the loop if no more data to send
                    if (bytesRead <= 0)
                    {
                        Console.WriteLine("\nAll data sent.");
                        endOfFile = true;
                        break;
                    }
                    string datapart = new string(buffer, 0, bytesRead);
                    //Make 4 number id (0001)
                    string formattedId = id.ToString("D4");
                    id++;
                    //Create a chunk with id and content
                    Message chunk = CreateMessage(MessageType.Data, $"{formattedId}{datapart}");
                    //Send the chunk to the client
                    SendMessages(currentClientEndpoint, chunk);

                    sentChunks.Add(chunk);
                    unackedIds.Add(formattedId);
                }

                // Check for acks and resend unacked data messages
                sock.ReceiveTimeout = 1000;
                while (unackedIds.Count > 0 && sendingData)
                {
                    Console.WriteLine("\nWaiting for ACKs...");
                    try
                    {
                        HandleClient();
                    }
                    catch (SocketException ex)
                    {
                        if (ex.SocketErrorCode == SocketError.TimedOut)
                        {
                            // Timeout, resend unaknowledged messages
                            missedacks = true;
                            Console.WriteLine("\nTimeout expired. Resending unacknowledged messages...");
                            windowSize = 1;
                            ResendData(currentClientEndpoint, unackedIds);
                        }
                        else
                        {
                            Console.WriteLine($"Socket error while waiting for ACKs: {ex.Message}");
                            ErrorMessage(currentClientEndpoint, $"Socket error while waiting for ACKs: {ex.Message}");
                            EndSession(currentClientEndpoint, false);
                            //Other socket errors
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error while waiting for ACKs: {ex.Message}");
                        ErrorMessage(currentClientEndpoint, $"Error while waiting for ACKs: {ex.Message}");
                        EndSession(currentClientEndpoint, false);
                    }
                }

                //Clear Id and chunk lists and double the windowsize if no acks were missed
                if (!endOfFile && sendingData)
                {
                    if (!missedacks)
                    {
                        Console.WriteLine("\nAll chunks acknowledged. Doubling window size.");
                        windowSize *= 2; //Double the amount of messages to send  
                    }
                    missedacks = false;
                    sentChunks.Clear(); //Empty the sent data messages list
                    unackedIds.Clear(); //Empty the missed ack messages list
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading file: {ex.Message}");
            ErrorMessage(currentClientEndpoint, $"Error reading file: {ex.Message}");
        }
        finally
        {
            if (sendingData)//If sending data did not end with an error, end the session with current client 
            {
                EndSession(currentClientEndpoint, true);
            }
        }
    }

    private void ResendData(EndPoint currentClientEndpoint, List<string> unackedIds)
    {
        try
        {
            int sentCount = 0;
            //Iterate through sentMessages and resend unacknowledged messages
            foreach (var message in sentChunks)
            {
                if (message.Content != null && message.Content.Length >= 4 && unackedIds.Contains(message.Content.Substring(0, 4)))
                {
                    //Resend the message
                    Console.WriteLine($"\nResending data: {JsonSerializer.Serialize(message)}");
                    SendMessages(currentClientEndpoint, message);
                }
                //Stop resending messages if reached windowsize
                if (sentCount >= windowSize)
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            ErrorMessage(currentClientEndpoint, "Error resending missed Data messages");
            EndSession(currentClientEndpoint, false);
        }
    }

    private void HandleAck(string content)
    {
        if (content.Length >= 4)
        {
            string ackId = content.Substring(0, 4);
            unackedIds.Remove(ackId); //Remove from unacknowledged messages
            Console.WriteLine($"\nACK received for ID: {ackId}");
        }
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

    private void SetThreshold(EndPoint remoteEP, string th)
    {
        try
        {
            int number = int.Parse(th);
            //Check if reasonalble number
            if (number < 51 && number > 0)
            {
                //Use the threshold integer value here
                threshold = number;
                Console.WriteLine("Threshold: " + threshold);
            }
            else
            {
                Console.WriteLine("Threshold not within 1-50 range");
                ErrorMessage(remoteEP, "Threshold not within 1-50 range");
                EndSession(remoteEP, false);
            }
        }
        catch (FormatException)
        {
            Console.WriteLine("The content is not a valid integer");
            ErrorMessage(remoteEP, "The content is not a valid integer");
            EndSession(remoteEP, false);
        }
    }

    private void EndSession(EndPoint remoteEP, bool sendEndmsg)
    {
        if (sendEndmsg)
        {
            //Create end message
            Message endMessage = CreateMessage(MessageType.End, "");

            //Send the end message
            SendMessages(remoteEP, endMessage);
        }
        //End the session with the client
        if (currentClientEndpoint != null && currentClientEndpoint.Equals(remoteEP))
        {
            Console.WriteLine("\nSession ended with client: " + currentClientEndpoint.ToString());
            currentClientEndpoint = null;

            //Reset the MessageType list
            expectedMessageTypes = new List<MessageType> { MessageType.Hello, MessageType.Error };
        }
        else
        {
            Console.WriteLine("\nNo active session with the client to end.");
            ErrorMessage(remoteEP, "No active session to end.");
        }
        sendingData = false;
    }

    private bool GetData(string data, EndPoint remoteEP)
    {
        //Check if file exists and if not empty
        if (!File.Exists(data))
        {
            Console.WriteLine($"\nData '{data}' not found.");
            ErrorMessage(remoteEP, $"Data '{data}' not found.");
            EndSession(remoteEP, false);
            return false;
        }
        return true;
    }

    private void ErrorMessage(EndPoint remoteEP, string msg)
    {
        //Create an error message
        Message errorMessage = CreateMessage(MessageType.Error, msg);

        //Send the error message
        SendMessages(remoteEP, errorMessage);
    }

    private void SendMessages(EndPoint receiver, Message message)
    {
        //Serialize the message to JSON
        string json = JsonSerializer.Serialize(message);
        Console.WriteLine($"\nSending data: {json}");

        //Convert the message to bytes
        byte[] BMessage = Encoding.ASCII.GetBytes(json);

        //Send the message to the client
        sock.SendTo(BMessage, BMessage.Length, SocketFlags.None, receiver);
    }
}
