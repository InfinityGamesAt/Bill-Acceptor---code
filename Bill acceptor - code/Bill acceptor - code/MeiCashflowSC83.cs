using MSSQL2008;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;

namespace Softparc.PeripheralsCommunication
{
    internal class MeiCashflowSC83 : PerifericsNormal
    {
        #region Dates about the ticket and the note
        [Serializable]
        private struct BillTicketInformation
        {
            /// <summary>
            /// this member is used to retain the value of a bill or to retain the number that is printer in code for ticket
            /// </summary>
            public long lBillTicketValue;

            /// <summary>
            /// this member is used only for ticket to put the value of the ticket that is in database(not the number that is on code bar)
            /// </summary>
            public float fTicketValueFromDatabase;

            /// <summary>
            /// this member retains true if a bill was inserted else retains false
            /// </summary>
            public bool bIsBill;
        };

        private struct BillsDenom
        {
            public int iValue;
            //public string szCountry;
            public bool bIsEnabled;
        }
        #endregion

        #region commands sent to MeiCashflow through serial port

        byte[] oEnableAllBill = { 0x02, 0x08, 0x30, 0x7F, 0x1C, 0x16, 0x03, 0x00 };//0x7f- all denomination enabled and barcode, extended note report, powerup B
        byte[] oReturnBill = { 0x02, 0x0A, 0x70, 0x02, 0x7F, 0x5C, 0x16, 0x00, 0x03, 0x00 };//returns a note--was changed byte 8 from 02 in 0      
        byte[] oRejectBill = { 0x02, 0x08, 0x10, 0x7F, 0x5C, 0x16, 0x03, 0x00 };//timer rejectbill
        byte[] oStatusRequest = { 0x02, 0x08, 0x30, 0x7F, 0x1C, 0x16, 0x03, 0x00 };//request the status of the acceptor
        byte[] oAcceptBill = { 0x02, 0x08, 0x30, 0x7F, 0x3C, 0x16, 0x03, 0x00 };
        // byte[] oAcceptBill ={ 0x02, 0x0A, 0x70, 0x02, 0x7F, 0x3C, 0x16, 0x00, 0x03, 0x00 };//da comanda de stack--was changed byte 8 from 02 in 0
        byte[] oInhibit = { 0x02, 0x11, 0x70, 0x03, 0x00, 0x1C, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00 };//inhibit all denom, bar code => inhibit acceptor
        //inhibit 10->14, restul 12->16
        byte[] oNoteSet = { 0x02, 0x0A, 0x70, 0x02, 0x7F, 0x1C, 0x16, 0x01, 0x03, 0x00 };//request note set 

        private byte[] requestSoftwareVersion = { 0x02, 0x08, 0x60, 0x00, 0x00, 0x07, 0x03, 0x00 };
        private byte[] requestSerialNumber = { 0x02, 0x08, 0x60, 0x00, 0x00, 0x05, 0x03, 0x00 };
        bool acceptCommandSent = false;
        bool acceptatAici = false;

        #endregion

        private bool sentNoStoacker = false;
        byte[] oByteStacking = new byte[40];
        int[] iBillCode = new int[9];

        const string szBillTicketInformation = "c:\\test.bin";
        List<BillsDenom> billDenom = new List<BillsDenom>();

        bool bRunninFirstTime = true;

        bool bRejectAllBills = false;

        bool bRejectingState = false;

        bool bFailure = false;

        bool sendNextError = true;

        bool bBookmark = false;

        bool bInChannel = false;

        bool bJam = true;

        bool bStackerFull = true;

        bool bAck = false;

        bool bStackerOpen = false;

        bool bExistError = false;

        bool bFirstTime = true;//first time start

        bool bBillAccepted = false;

        bool bEscrow = false;//a bill is inserted

        bool bExtendedEscrow = false;//bill in escrow waitting for command - accept/return

        bool bStacking = false;

        bool bInhibit = false;

        DateTime? dtWhenInEscrow = null;

        System.Timers.Timer timerRejectBill = new System.Timers.Timer(1000);

        System.Timers.Timer timerFirstTime = new System.Timers.Timer();//delay timer at the beginning of bill acceptor to not given money before the game start starting the bill acceptor before the game

        #region Here are set the serial port , socket and timer used in the class
        public MeiCashflowSC83(System.IO.Ports.SerialPort sp, System.Timers.Timer timer)
            : base(sp, timer)
        {
            this.OnBillStacked += MeiCashflowSC83_OnBillStacked;
        }

        bool sendStacked = true;

        void MeiCashflowSC83_OnBillStacked(object sender, BillStackedEventArgs e)
        {
            if (sendStacked && acceptCommandSent)
            {
                Thread enableAccepting = new Thread(() =>
                {
                    Thread.Sleep(300);
                    acceptCommandSent = false;
                });
                enableAccepting.Start();
                sendStacked = false;
                if (Buffer[4] == 0x11 && !bStackerOpen && !acceptatAici) //daca nu ii mesaj de stack dupa repunerea stackerului la loc
                {
                    Logger.Write("Byte ------------------ a intrat in Stack", e.buffer, 0, BytesReadFromPerif);
                    //if (!timerFirstTime.Enabled)
                    if (e.buffer[3] == 0x02) DealWithStackedEvents(e.buffer);//for bill
                }
                if (Buffer[3] == 0x11 && !bStackerOpen && !acceptatAici)
                {
                    byte[] newBuffer = new byte[e.buffer.Length + 1];
                    Array.Copy(e.buffer, 0, newBuffer, 1, e.buffer.Length);
                    newBuffer[0] = 0x02;
                    if (e.buffer[3] == 0x02) DealWithStackedEvents(newBuffer);//for bill
                }
                Thread t = new Thread(() =>
                {
                    Thread.Sleep(500);
                    sendStacked = true;
                });
                t.Start();
            }
        }
        public override void Initialize()
        {
            base.Initialize();

            try
            {
                //disable the timer event Elapsed
                EnabledElapsedFromTimer = false;
                //disable the DataRecived event from serial port
                EnableDataRecivedFromSerial = false;
                //disable resending the last command
                ResendLastCommandWhenTimeout = false;

                this.OnDataRecivedFromGame += new DataRecivedFromGameDelegate(MeiCashflowSC83_OnDataRecivedFromGame);
                this.OnDataRecivedFromPerifericTimer += new DataRecivedFromPerifericTimerDelegate(MeiCashflowSC83_OnDataRecivedFromPerifericTimer);

                this.OnTimeout += new TimeoutDelegate(MeiCashflowSC83_OnTimeout);
                this.OnRecoverTimeout += new TimeoutRecoverDelegate(MeiCashflowSC83_OnRecoverTimeout);

                this.timerRejectBill.Elapsed += new System.Timers.ElapsedEventHandler(timerRejectBill_Elapsed);
                this.timerFirstTime.Elapsed += new System.Timers.ElapsedEventHandler(timerFirstTime_Elapsed);
            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in constructor", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }
        }

        void timerFirstTime_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            //try
            //{
            //    Logger.Write("Mei cashflow", "s-a intrat in timerFirstTime");
            //    timerFirstTime.Enabled = false;
            //    System.Threading.Thread.Sleep(2000);
            //    DealWithStackedEvents(oByteStacking);
            //}
            //catch (Exception ex)
            //{
            //    Logger.Write("Exceptie in FirstTime_Elapsed", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            //}
        }

        void timerRejectBill_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                EnabledElapsedFromTimer = false;
                timerRejectBill.Enabled = false;
                if (dtWhenInEscrow != null)
                {
                    dtWhenInEscrow = null;
                    bAck = !bAck;
                    oRejectBill[2] = SetBit(oRejectBill[2], 0, bAck);
                    //WriteToPort(oReturnBill);
                    WriteToPort(oRejectBill);
                    Logger.Write("In timer reject bill", "s-a data return bill command");
                    ReadFromSerialPortInternal();
                    EnabledElapsedFromTimer = true;
                    if (!((!IsBitSet(Buffer[2], 0) && !bAck) || (IsBitSet(Buffer[2], 0) && bAck)) && ValidChkSum(Buffer, Buffer[BytesReadFromPerif - 1]))
                    {
                        //error in reciveing status Ack
                        Logger.Write("Mei Cashflow error", "nu este Ack in return bill dupa accept bill");
                        return;
                    }
                    Logger.Write("Mei Cashflow", "The bill was not accepted");
                }
            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in timerRejectBill", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }
        }
        #endregion



        #region Here are recieved and interpreted the messages from game
        void MeiCashflowSC83_OnDataRecivedFromGame(object sender, PerifericDataRecivedFromGameEvent e)
        {
            try
            {
                GameToPerif gtp = e.GameToPerifericData;
                Logger.Write("A message from the game was received in Mei Cashflow BillAcceptor", gtp.Message);
                Logger.Write("A message from the game was received in Mei Cashflow BillAcceptor", gtp.Cmd.ToString());
                Logger.Write("A message from the game was received in Mei Cashflow BillAcceptor", gtp.Value.ToString());
                if (Timeout) return;
                if (gtp.Cmd == CmdGameToPerif.oBillVersion)
                {

                }
                if (gtp.Cmd == CmdGameToPerif.oChangeStatus)
                {
                    if ((gtp.Value & 0x0200) != 0)//enable/disable bill type
                    {
                        int iValue = Convert.ToInt32(gtp.Msg[0] * Math.Pow(10, gtp.Msg[1]));
                        BillsDenom bill;
                        if (billDenom.FindIndex(x => x.iValue == iValue) <= -1)
                        {
                            BillsDenom temp = new BillsDenom();
                            temp.bIsEnabled = true;
                            temp.iValue = iValue;
                            billDenom.Add(temp);
                        }
                        for (int i = 0; i < billDenom.Count; i++)
                            if (billDenom[i].iValue == iValue)
                            {
                                bill = billDenom[i];
                                if (gtp.Status == 0x00)
                                    bill.bIsEnabled = true;
                                else bill.bIsEnabled = false;
                                billDenom[i] = bill;
                                break;
                            }
                        return;
                    }
                    if ((gtp.Value & 0x0400) != 0) // enable/disable all denominations
                    {
                        Logger.Write("in reject all bills cmd", "status este:" + gtp.Status.ToString());
                        if (gtp.Status == 0x00) bRejectAllBills = true;
                        else bRejectAllBills = false;
                    }

                    if ((gtp.Value & 0x20) != 0)// bill acceptor
                    {
                        if (bStackerOpen)
                            return;
                        if (gtp.Status == 0x00)
                        {
                            Logger.Write("Mei Cashflow", "Mei Cashflow change status");
                            isInhibit = false;
                            if (bInhibit)
                            {
                                bInhibit = false;
                                return;
                            }
                            InitMei();
                            EnabledElapsedFromTimer = true;
                        }

                        if (gtp.Status == 0xFF)
                        {
                            if (!bStacking)//if is not in the bill stack process
                            {
                                Logger.Write("s-a intrat la Inhibit", "");

                                bFirstTime = true;
                                bAck = !bAck;
                                oRejectBill[2] = SetBit(oRejectBill[2], 0, bAck);

                                WriteToPort(oRejectBill);
                                ReadFromSerialPortInternal();

                                oInhibit[2] = SetBit(oInhibit[2], 0, bAck);
                                WriteToPort(oInhibit);
                                isInhibit = true;
                                ReadFromSerialPortInternal();

                                if (!((!IsBitSet(Buffer[2], 0) && !bAck) || (IsBitSet(Buffer[2], 0) && bAck)) && ValidChkSum(Buffer, Buffer[10]))
                                {
                                    Logger.Write("Mei Cashflow", "No response to oInhibit cmd in OnDataReceivedFromGame and disable acceptor");
                                }
                                else
                                    Logger.Write("Buffer dupa comanda Inhibit", Buffer, 0, BytesReadFromPerif);

                                //if (IsBitSet(Buffer[3], 2))//escrow dupa inhibit
                                //{
                                //    bAck = !bAck;
                                //    oRejectBill[2] = SetBit(oRejectBill[2], 0, bAck);
                                //    WriteToPort(oRejectBill);
                                //    ReadFromSerialPort();
                                //    Logger.Write("Mei Cashflow S-a dat return la bill dupa Inhibit", Buffer, 0, BytesReadFromPerif);
                                //}

                                bAck = !bAck;
                                bInhibit = false;
                            }
                            else
                            {
                                Logger.Write("bInhibit", "true");
                                bInhibit = true;
                                return;
                            }
                        }
                    }
                }

                if (gtp.Cmd == CmdGameToPerif.oAcceptBillTicket && dtWhenInEscrow != null)
                {
                    timerRejectBill.Enabled = false;
                    Logger.Write("Mei Cashflow", "After accept bill cmd sent from game ");
                    TimeSpan tsp = DateTime.Now - dtWhenInEscrow.Value;
                    dtWhenInEscrow = null;

                    EnabledElapsedFromTimer = false;
                    System.Threading.Thread.Sleep(200);

                    if (tsp.TotalSeconds < 1)
                    {
                        if (!acceptCommandSent)
                        {
                            Logger.Write("In accept tsp < 1", "abc");
                            oAcceptBill[2] = SetBit(oAcceptBill[2], 0, bAck);
                            Logger.Write("Mesaj trimis catre acceptor in accept", oAcceptBill);
                            acceptCommandSent = true;

                            WriteToPort(oAcceptBill);
                            ReadFromSerialPortInternal();

                            Logger.Write("Mei Cashflow", "The accept bill command was sent");
                            bBillAccepted = true;
                            bStacking = true;

                            oStatusRequest[2] = SetBit(oStatusRequest[2], 0, !bAck);
                            WriteToPort(oStatusRequest);
                            ReadFromSerialPortInternal();
                            Logger.Write("CE s-a citit dupa comanda de ACCEPT BILL: ", Buffer, 0, BytesReadFromPerif);
                        }
                    }
                    else
                        if (!bBillAccepted)
                        {
                            bAck = !bAck;
                            oReturnBill[2] = SetBit(oReturnBill[2], 0, bAck);
                            WriteToPort(oReturnBill);
                            ReadFromSerialPortInternal();
                            if (!((!IsBitSet(Buffer[2], 0) && !bAck) || (IsBitSet(Buffer[2], 0) && bAck)) && ValidChkSum(Buffer, Buffer[BytesReadFromPerif - 1]))//Buffer[10] in loc de Buffer.Length
                            {
                                //error in reciveing status Ack
                                Logger.Write("Mei Cashflow", "nu este Ack in comanda return bill ");
                                dtWhenInEscrow = null;
                                EnabledElapsedFromTimer = true;

                                return;
                            }
                            Logger.Write("Mei Cashflow", "The bill was not accepted");
                        }

                    EnabledElapsedFromTimer = true;
                }
            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in DataReceivedFromGame", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }
        }
        #endregion

        #region Here are recieved and interpreted the messages that are recieved from MEI
        void MeiCashflowSC83_OnDataRecivedFromPerifericTimer(object sender, PerifericDataRecivedFromPerifericTimerEvent e)
        {
            try
            {
                if (isInhibit)
                    return;
                Logger.Write("la intrarea in datareceived from periferic timer: ", Buffer, 0, BytesReadFromPerif);

                if (!(Buffer[2] == 0x70 || Buffer[2] == 0x71))//test if is extended message or not
                {
                    if (!IsBitSet(Buffer[5], 1) && Buffer[0] == 0x02 && Buffer[9] == 0x03)//if a valid response was received from acceptor
                    {
                        if (((!IsBitSet(Buffer[2], 0) && !bAck) || (IsBitSet(Buffer[2], 0) && bAck)) && ValidChkSum(Buffer, Buffer[10]))//if message Acknowledged by the bill acceptor & message received ok (correct checksum)
                        {
                            bAck = !bAck;
                            PerifToGame ptg = new PerifToGame();
                            ptg.Cmd = CmdPerifToGame.BillAcceptor;

                            bRejectingState = false;

                            if (!IsBitSet(Buffer[3], 0))//not idle
                            {

                                Logger.Write("Mei Cashflow Nu este in Idle", Buffer, 0, BytesReadFromPerif);

                                if (IsBitSet(Buffer[3], 1) || IsBitSet(Buffer[3], 2))//escrow pentru Inhibit
                                {
                                    bEscrow = true;
                                }

                                if (bExistError)
                                {
                                    Logger.Write("Mei Cashflow", "se incearca repornirea acceptorului");
                                    InitMei();
                                    return;
                                }

                                //if (!IsBitSet(Buffer[3], 2)) bBookmark = false;//when bite escrow is not set
                                if (IsBitSet(Buffer[3], 2) && bBookmark)//second bookmark
                                {
                                    oReturnBill[2] = SetBit(oReturnBill[2], 0, bAck);
                                    EnabledElapsedFromTimer = false;
                                    WriteToPort(oReturnBill);
                                    ReadFromSerialPortInternal();
                                    Logger.Write("Mei Cashflow", "S-a dat return la al doilea bookmark");

                                    if (!((!IsBitSet(Buffer[2], 0) && !bAck) || (IsBitSet(Buffer[2], 0) && bAck)) && ValidChkSum(Buffer, Buffer[BytesReadFromPerif - 1]))
                                    {
                                        //error in recieving status Ack
                                        Logger.Write("MeiCashflow", "mesajul nu a fost Ack de catre bill acceptor");
                                        EnabledElapsedFromTimer = true;
                                        return;
                                    }
                                    ptg.Status = 0x05;
                                    SendToGame(ptg.Message);
                                    bAck = !bAck;
                                    EnabledElapsedFromTimer = true;
                                    return;
                                }

                                if (IsBitSet(Buffer[3], 2) && !bBookmark)//for bookmark when the byte escrow is set
                                {
                                    if (bRejectAllBills)
                                    {
                                        oReturnBill[2] = SetBit(oReturnBill[2], 0, bAck);
                                        EnabledElapsedFromTimer = false;
                                        WriteToPort(oReturnBill);
                                        ReadFromSerialPortInternal();
                                        Logger.Write("Mei Cashflow", "S-a dat return la primul bookmark");
                                        if (!((!IsBitSet(Buffer[2], 0) && !bAck) || (IsBitSet(Buffer[2], 0) && bAck)) && ValidChkSum(Buffer, Buffer[BytesReadFromPerif - 1]))
                                        {
                                            //error in recieving status Ack
                                            Logger.Write("MeiCashflow", "mesajul nu a fost Ack de catre bill acceptor");
                                            EnabledElapsedFromTimer = true;
                                            return;
                                        }
                                        ptg.Status = 0x05;
                                        SendToGame(ptg.Message);
                                        bAck = !bAck;
                                        EnabledElapsedFromTimer = true;
                                        return;
                                    }

                                    oAcceptBill[2] = SetBit(oAcceptBill[2], 0, bAck);
                                    EnabledElapsedFromTimer = false;
                                    WriteToPort(oAcceptBill);

                                    ReadFromSerialPortInternal();
                                    if (!((!IsBitSet(Buffer[2], 0) && !bAck) || (IsBitSet(Buffer[2], 0) && bAck)) && ValidChkSum(Buffer, Buffer[BytesReadFromPerif - 1])) //command was not accepted by bill acceptor change 7 to Buffer[10]
                                    {
                                        //error in reciveing status Ack
                                        Logger.Write("Mei Cashflow error", "nu este Ack dupa AcceptBill  ");
                                        EnabledElapsedFromTimer = true;
                                        return;
                                    }
                                    Logger.Write("Mei cashflow", "S-a bagat un BOOKMARK");
                                    bBookmark = true;
                                    bAck = !bAck;
                                    EnabledElapsedFromTimer = true;
                                    return;

                                }

                                if (IsBitSet(Buffer[4], 0)) //cheat attempt
                                {
                                    Logger.Write("Mei Cashflow", "Cheat attempt");
                                    ptg.Status = 0x06;
                                    SendToGame(ptg.Message);
                                }

                                if (IsBitSet(Buffer[4], 1))//bill rejected
                                {
                                    Logger.Write("Mei Cashflow", "Bill rejected");
                                    ptg.Status = 0x04;
                                    SendToGame(ptg.Message);
                                }

                                if (IsBitSet(Buffer[4], 2) && !bJam) //bill jam
                                {
                                    Logger.Write("Mei Cashflow", "Bill jam");
                                    byte[] oMsg = new byte[8];
                                    oMsg[3] = SetBit(oMsg[3], 0, true);
                                    ptg.Status = 0xF9;
                                    ptg.Msg = oMsg;
                                    SendToGame(ptg.Message);
                                    Logger.Write("Buffer : ", Buffer, 0, BytesReadFromPerif);
                                    bJam = true;
                                    return;
                                }

                                if (!IsBitSet(Buffer[4], 3)) bStackerFull = false;
                                if (IsBitSet(Buffer[4], 3) && !bStackerFull)//cassette full 
                                {
                                    Logger.Write("Mei Cashflow", "Stacker full");
                                    ptg.Status = 0xF9;
                                    byte[] oMsg = new byte[8];
                                    oMsg[3] = SetBit(oMsg[3], 2, true);
                                    ptg.Msg = oMsg;
                                    SendToGame(ptg.Message);
                                    bStackerFull = true;
                                    return;
                                }

                                if (IsBitSet(Buffer[4], 4)) bStackerOpen = false;

                                //le-am mutat direct
                                //if (!IsBitSet(Buffer[4], 4) && !bStackerOpen) //daca s-a scos stackerul si bStackerOpen=false-pt a nu retransmite mesajul
                                //{
                                //    Logger.Write("Mei Cashflow", "S-a scos Stackerul!!!!");
                                //    ptg.Status = 0xF9;
                                //    byte[] oMsg = new byte[8];
                                //    oMsg[3] = SetBit(oMsg[3], 1, true);
                                //    ptg.Msg = oMsg;
                                //    SendToGame(ptg.Message);
                                //    bBookmark = false;
                                //    bStackerOpen = true;
                                //    return;
                                //}
                                //if (Buffer[4] == 0x00 && sentNoStoacker)
                                //{

                                //    ptg.Status = 0xF9;
                                //    byte[] oMsg = new byte[8];
                                //    oMsg[3] = SetBit(oMsg[3], 5, true);
                                //    ptg.Msg = oMsg;
                                //    SendToGame(ptg.Message);
                                //    sentNoStoacker = false;
                                //    return;
                                //}
                                if (!IsBitSet(Buffer[5], 2))
                                {
                                    sendNextError = true;
                                    bFailure = false;//if are 0 errors
                                }
                                if (IsBitSet(Buffer[5], 2) && !bFailure)//if we have unknown error
                                {
                                    Logger.Write("Mei Cashflow", "Bill acceptor failure ");
                                    if (!sendNextError)
                                    {
                                        ptg.Status = 0xF9;
                                        byte[] oMsg = new byte[8];
                                        oMsg[3] = SetBit(oMsg[3], 4, true);
                                        ptg.Msg = oMsg;
                                        SendToGame(ptg.Message);

                                        bFailure = true;
                                        if (System.IO.File.Exists(szBillTicketInformation)) System.IO.File.Delete(szBillTicketInformation);//if the file exists delete the file
                                    }
                                    sendNextError = true;
                                    return;
                                }
                            }
                            else //idle
                            {
                                Logger.Write("Mei cashflow", "IDLE");
                                bStacking = false;

                                if (!IsBitSet(Buffer[4], 2)) bJam = false;

                                if (IsBitSet(Buffer[3], 4) && bStackerOpen)
                                {
                                    bExistError = true;
                                    //daca s-a pus stackerul la loc (se primeste mesaj cu bitu de escrow setat)
                                }

                                if (IsBitSet(Buffer[3], 6))//daca s-a returnat o bancnota sau ticket, se sterge fisierul cu datele ticketului, daca exista
                                {
                                    if (System.IO.File.Exists(szBillTicketInformation)) System.IO.File.Delete(szBillTicketInformation);//se sterge fisierul
                                }

                                if (IsBitSet(Buffer[3], 4))
                                {
                                    DealWithStackedEvents(); //pt ticket

                                }

                                if ((bExistError || bRunninFirstTime) && (!Timeout))
                                {
                                    Logger.Write("Mei Cashflow", "in Idle state");
                                    ptg.Status = 0x01;
                                    bExistError = false;
                                    bRunninFirstTime = false;
                                    bStackerFull = false;
                                    bJam = false;
                                    SendToGame(ptg.Message);
                                    return;
                                }

                                if (IsBitSet(Buffer[4], 4)) bStackerOpen = false;

                                //if (!IsBitSet(Buffer[4], 4) && !bStackerOpen && !sentNoStoacker) //daca s-a scos stackerul si bStackerOpen=false-pt a nu retransmite mesajul
                                //{
                                //    sentNoStoacker = true;
                                //    Logger.Write("Mei Cashflow", "S-a scos Stackerul!!!!");
                                //    ptg.Status = 0xF9;
                                //    byte[] oMsg = new byte[8];
                                //    oMsg[3] = SetBit(oMsg[3], 1, true);
                                //    ptg.Msg = oMsg;
                                //    SendToGame(ptg.Message);
                                //    bBookmark = false;
                                //    bStackerOpen = true;
                                //    return;
                                //}
                                //if (IsBitSet(Buffer[4], 4) && sentNoStoacker)
                                //{
                                //    ptg.Status = 0xF9;
                                //    byte[] oMsg = new byte[8];
                                //    oMsg[3] = SetBit(oMsg[3], 5, true);
                                //    ptg.Msg = oMsg;
                                //    SendToGame(ptg.Message);
                                //    sentNoStoacker = false;
                                //    return;
                                //}
                                if (IsBitSet(Buffer[4], 1))//bill rejected
                                {
                                    Logger.Write("Mei Cashflow", "Bill rejected in IDLE");
                                    ptg.Status = 0x04;
                                    SendToGame(ptg.Message);
                                }
                            }
                        }
                    }
                    else //wrong command format
                    {
                        //bAck = !bAck;  //daca comanda se retransmite, Ack nu se schimba
                        Logger.Write("Mei Cashflowwwww comanda primita nu este valida", Buffer);
                        return;
                    }
                }
                else//s-a introdus bancnota sau ticket
                {
                    if ((Buffer[1] == BytesReadFromPerif) && ValidChkSum(Buffer, Buffer[BytesReadFromPerif - 1]))  //daca s-a citit buffer de lungime buna si cu checksum valid
                    {
                        PerifToGame ptg = new PerifToGame();
                        ptg.Cmd = CmdPerifToGame.BillAcceptor;
                        Logger.Write("BUffer in extended: ", Buffer, 0, BytesReadFromPerif);
                        if (bStackerOpen) bExistError = true;//daca este scos stackerul, exista eroare
                        if (!IsBitSet(Buffer[4], 4))//daca nu ii setat bitu de stacked
                        {
                            //bStacked = false;
                            if ((IsBitSet(Buffer[4], 2)) && !bRejectingState)//test if escrow state si bitu stacked sa nu fie setat si sa nu fi intrat in rejectall bills ultima data, ca sa afiseze mesajul doar o data pt fiecare ticket introdus     
                            {
                                //bCheatState = false;
                                if ((bRejectAllBills) && !bBillAccepted) //daca este o usa deschisa sau reject all bills dar nu s-a dat accept bill
                                {
                                    oReturnBill[2] = SetBit(oReturnBill[2], 0, bAck);
                                    EnabledElapsedFromTimer = false;
                                    WriteToPort(oReturnBill);
                                    ReadFromSerialPortInternal();
                                    Logger.Write("Mei Casfhlow", "S-a dat return in extended");
                                    if (!((!IsBitSet(Buffer[2], 0) && !bAck) || (IsBitSet(Buffer[2], 0) && bAck)) && ValidChkSum(Buffer, Buffer[BytesReadFromPerif - 1]))
                                    {
                                        //error in recieving status Ack
                                        Logger.Write("MeiCashflow", "mesajul nu a fost Ack de catre bill acceptor");
                                        EnabledElapsedFromTimer = true;
                                        return;
                                    }
                                    ptg.Status = 0x05;
                                    SendToGame(ptg.Message);
                                    bAck = !bAck;
                                    bRejectingState = true;
                                    EnabledElapsedFromTimer = true;
                                    return;

                                }
                                if (Buffer[3] == 0x02)//daca ii bancnota
                                {
                                    Logger.Write("Valoarea bancnotei este: ", Buffer, 0, BytesReadFromPerif);
                                    double dBillValue = 0;
                                    try
                                    {
                                        dBillValue = (int.Parse(Encoding.ASCII.GetString(Buffer, 14, 3))) * Math.Pow(10, int.Parse(Encoding.ASCII.GetString(Buffer, 18, 2)));
                                    }
                                    catch
                                    {
                                    }
                                    for (int i = 0; i < billDenom.Count; i++)
                                        if (billDenom[i].iValue == dBillValue)
                                        {
                                            if (!billDenom[i].bIsEnabled)
                                            {
                                                oReturnBill[2] = SetBit(oReturnBill[2], 0, bAck);
                                                EnabledElapsedFromTimer = false;
                                                WriteToPort(oReturnBill);
                                                ReadFromSerialPortInternal();
                                                Logger.Write("Mei Casfhlow", "S-a dat return in test daca ii bancnota");
                                                if (!((!IsBitSet(Buffer[2], 0) && !bAck) || (IsBitSet(Buffer[2], 0) && bAck)) && ValidChkSum(Buffer, Buffer[BytesReadFromPerif - 1]))
                                                {
                                                    Logger.Write("Mei Cashflow", "Whithout Ack after sending the ReturnBill command in Escrow state when a bill is inserted");
                                                    EnabledElapsedFromTimer = true;
                                                    return;
                                                }
                                                ptg.Cmd = CmdPerifToGame.BillAcceptor;
                                                ptg.Status = 0x05;
                                                SendToGame(ptg.Message);
                                                oStatusRequest[2] = SetBit(oStatusRequest[2], 0, !bAck);
                                                WriteToPort(oStatusRequest);
                                                ReadFromSerialPortInternal();
                                                EnabledElapsedFromTimer = true;
                                                return;
                                            }

                                            break;
                                        }
                                    if (dtWhenInEscrow == null)
                                    {

                                        Logger.Write("Mei Cashflow", "Sending the message to game- bill is waiting in escrow");

                                        //bStacking = true;

                                        bExtendedEscrow = true;

                                        byte[] oVal = new byte[8];

                                        for (int i = 0; i < oVal.Length; i++)
                                            oVal[i] = 0x30;
                                        string s = Convert.ToString(dBillValue);
                                        byte[] test = Encoding.ASCII.GetBytes(s);

                                        for (int i = test.Length - 1, j = oVal.Length - 1; i >= 0; i--, j--)
                                            oVal[j] = test[i];

                                        ptg.Cmd = CmdPerifToGame.BillAcceptor;
                                        ptg.Status = 0x03;
                                        ptg.Value = oVal;
                                        ptg.MsgLen = 0;
                                        dtWhenInEscrow = DateTime.Now;
                                        SendToGame(ptg.Message);
                                        timerRejectBill.Enabled = true;
                                    }
                                }
                                else//ticket
                                {
                                    BillTicketInformation ticket = new BillTicketInformation();
                                    ticket.bIsBill = false;
                                    ticket.lBillTicketValue = 0;
                                    byte[] oMessage = new byte[18];
                                    for (int i = 10; i < 28; i++)
                                    {
                                        oMessage[i - 10] = Buffer[i];
                                        ticket.lBillTicketValue = ticket.lBillTicketValue * 10 + Buffer[i] - 48;
                                    }
                                    Logger.Write("Mei Cashflow oMessage trimis: ", oMessage);
                                    ptg.Msg = oMessage;
                                    if (!IsInDatabase(ticket.lBillTicketValue.ToString()))//nu e in baza de date
                                    {
                                        oReturnBill[2] = SetBit(oReturnBill[2], 0, bAck);

                                        EnabledElapsedFromTimer = false;
                                        WriteToPort(oReturnBill);
                                        ReadFromSerialPortInternal();

                                        //PerifericsLogger.Write("Raspuns la comanda retrun daca nu e in baza de date: ", Buffer, 0, BytesReadFromPerif);
                                        if (!((!IsBitSet(Buffer[2], 0) && !bAck) || (IsBitSet(Buffer[2], 0) && bAck)) && ValidChkSum(Buffer, Buffer[BytesReadFromPerif - 1]))
                                        {

                                            Logger.Write("Mei Cashflow", "Whithout Ack after Return ticket command when a ticket is not in database");
                                            EnabledElapsedFromTimer = true;
                                            return;
                                        }

                                        ptg.Status = 0xFC;

                                        SendToGame(ptg.Message);
                                        oStatusRequest[2] = SetBit(oStatusRequest[2], 0, !bAck);
                                        WriteToPort(oStatusRequest);
                                        ReadFromSerialPortInternal();
                                        EnabledElapsedFromTimer = true;

                                        return;
                                    }
                                    if (!IsNotExpired(ticket.lBillTicketValue.ToString()))//ii expirat
                                    {
                                        oReturnBill[2] = SetBit(oReturnBill[2], 0, bAck);
                                        EnabledElapsedFromTimer = false;
                                        WriteToPort(oReturnBill);
                                        ReadFromSerialPortInternal();
                                        if (!((!IsBitSet(Buffer[2], 0) && !bAck) || (IsBitSet(Buffer[2], 0) && bAck)) && ValidChkSum(Buffer, Buffer[BytesReadFromPerif - 1]))
                                        {

                                            Logger.Write("Mei Cashflow", "Whithout Ack after Return ticket command when a ticket is expired");
                                            EnabledElapsedFromTimer = true;
                                            return;
                                        }
                                        ptg.Status = 0xFB;
                                        SendToGame(ptg.Message);

                                        oStatusRequest[2] = SetBit(oStatusRequest[2], 0, !bAck);
                                        WriteToPort(oStatusRequest);
                                        ReadFromSerialPortInternal();
                                        EnabledElapsedFromTimer = true;

                                        return;
                                    }

                                    ticket.fTicketValueFromDatabase = (float)GetTicketValue(ticket.lBillTicketValue.ToString());
                                    global::System.IO.FileStream sw = global::System.IO.File.Create(szBillTicketInformation);
                                    System.Runtime.Serialization.Formatters.Binary.BinaryFormatter bfWrite = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                                    bfWrite.Serialize(sw, ticket);
                                    sw.Close();

                                    if (dtWhenInEscrow == null)
                                    {
                                        bStacking = true;

                                        float fValue = ticket.fTicketValueFromDatabase;
                                        string szValue = fValue.ToString("F2");
                                        //for (int k = 0; k < Buffer[1]; k++)
                                        //    oByteStacking[k] = Buffer[k];

                                        int i, j;
                                        for (i = 0; i < oMessage.Length; i++)
                                            oMessage[i] = 0x30;

                                        for (i = szValue.Length - 1, j = oMessage.Length - 1; i >= 0; i--, j--)
                                        {
                                            oMessage[j] = Convert.ToByte(szValue[i]);
                                        }
                                        Logger.Write("Mei Cashflow oMessage trimis: ", oMessage);
                                        ptg.Msg = oMessage;
                                        ptg.Status = 0x03;
                                        ptg.MsgLen = 1;
                                        dtWhenInEscrow = DateTime.Now;
                                        SendToGame(ptg.Message);
                                        timerRejectBill.Enabled = true;
                                    }
                                }
                            }
                        }
                        if (IsBitSet(Buffer[4], 4) && bFirstTime)//daca s-a pornit jocu si bill acceptorul si s-a dat stack
                        {
                            bFirstTime = false;
                            for (int i = 0; i <= BytesReadFromPerif; i++)
                                oByteStacking[i] = Buffer[i];
                            timerFirstTime.Enabled = true;
                            oStatusRequest[2] = SetBit(oStatusRequest[2], 0, bAck);
                            EnabledElapsedFromTimer = false;
                            WriteToPort(oStatusRequest);
                            ReadFromSerialPortInternal();
                            Logger.Write("Bill in first time, buffer=", Buffer);
                            bAck = !bAck;
                            EnabledElapsedFromTimer = true;
                            return;
                        }
                        //if (IsBitSet(Buffer[4], 4) && !bStackerOpen && !acceptatAici) //daca nu ii mesaj de stack dupa repunerea stackerului la loc
                        //{
                        //    Logger.Write("Byte ------------------ a intrat in Stack", Buffer, 0, BytesReadFromPerif);
                        //    if (Buffer[3] == 0x02) DealWithStackedEvents();//pentru bancnota
                        //    System.Threading.Thread.Sleep(1000);
                        //}
                        if (acceptatAici && IsBitSet(Buffer[4], 4))
                        {
                            acceptatAici = false;
                        }

                        oStatusRequest[2] = SetBit(oStatusRequest[2], 0, bAck);
                        EnabledElapsedFromTimer = false;
                        WriteToPort(oStatusRequest);
                        ReadFromSerialPortInternal();
                        bAck = !bAck;

                        EnabledElapsedFromTimer = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in DataReceivedFromPerifericTimer", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }
        }
        #endregion

        #region Here are OnTimeout and OnRecoverTimeout events
        void MeiCashflowSC83_OnRecoverTimeout(object sender, PerifericTimeoutRecoverEvent e)
        {
            try
            {
                Logger.Write("Mei Cashflow", "recover from timeout error");
                InitMei();
                this.OnDataRecivedFromPerifericTimer -= new DataRecivedFromPerifericTimerDelegate(MeiCashflowSC83_OnDataRecivedFromPerifericTimer);//pt nedublarea mesajului dupa recover from communication timeout
                this.OnDataRecivedFromPerifericTimer += new DataRecivedFromPerifericTimerDelegate(MeiCashflowSC83_OnDataRecivedFromPerifericTimer);
            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in OnRecoverTimeout", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }
        }

        void MeiCashflowSC83_OnTimeout(object sender, PerifericTimeoutEvent e)
        {
            try
            {
                this.OnDataRecivedFromPerifericTimer -= MeiCashflowSC83_OnDataRecivedFromPerifericTimer;
                Logger.Write("Mei Cashflow", " a timeout error has ocurred");
                PerifToGame ptg = new PerifToGame();
                ptg.Cmd = CmdPerifToGame.BillAcceptor;
                ptg.Status = 0xFE;
                bExistError = true;
                SendToGame(ptg.Message);
            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in OnTimeout", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }
        }
        #endregion
        //bool trimite = true;
        public void ReadFromSerialPortInternal()
        {
            ReadFromSerialPort();
        }
        #region Other functions used in this class
        private void InitMei()
        {
            try
            {
                bInChannel = false;
                bFailure = false;
                bStackerOpen = false;
                bBillAccepted = false;
                PerifToGame ptg = new PerifToGame();
                ptg.Cmd = CmdPerifToGame.BillAcceptor;
                oStatusRequest[2] = SetBit(oStatusRequest[2], 0, bAck);
                WriteToPort(oStatusRequest);//reads bill acceptor status
                ReadFromSerialPortInternal();
                Logger.Write("Buffer dupa status request", Buffer, 0, BytesReadFromPerif);
                bAck = !bAck;
                if (Buffer[0] == 0x02 && Buffer[9] == 0x03) //daca este mesaj standard corect
                {
                    if (!IsBitSet(Buffer[3], 2)) bInChannel = false;//daca nu este bancnota in acceptor la pornire
                    if (IsBitSet(Buffer[5], 0) && IsBitSet(Buffer[3], 2) && !bInChannel)//power-up  
                    {
                        Logger.Write("Mei Cashflow", "Bill acceptor powered up with bill in it");
                        byte[] oMsg = new byte[8];
                        oMsg[3] = SetBit(oMsg[3], 0, true);
                        ptg.Status = 0xF9;
                        ptg.Msg = oMsg;
                        SendToGame(ptg.Message);
                        bInChannel = true;
                        return;
                    }

                    if (IsBitSet(Buffer[4], 2)) //bill jam
                    {
                        Logger.Write("Mei Cashflow", "Bill jam la pornirea acceptorului");
                        byte[] oMsg = new byte[8];
                        oMsg[3] = SetBit(oMsg[3], 0, true);
                        ptg.Status = 0xF9;
                        ptg.Msg = oMsg;
                        SendToGame(ptg.Message);
                        Logger.Write("Buffer : ", Buffer, 0, BytesReadFromPerif);
                        return;
                    }
                    if (IsBitSet(Buffer[4], 3))//cassette full 
                    {
                        Logger.Write("Mei Cashflow", "Stacker full la pornirea acceptorului");
                        ptg.Status = 0xF9;
                        byte[] oMsg = new byte[8];
                        oMsg[3] = SetBit(oMsg[3], 2, true);
                        ptg.Msg = oMsg;
                        SendToGame(ptg.Message);
                        return;
                    }

                    if (IsBitSet(Buffer[5], 2) && !bFailure)//daca este vreo eroare necunoscuta
                    {
                        Logger.Write("Mei Cashflow", "Bill acceptor failure la pornirea aparatului");
                        ptg.Status = 0xF9;
                        byte[] oMsg = new byte[8];
                        oMsg[3] = SetBit(oMsg[3], 4, true);
                        ptg.Msg = oMsg;
                        SendToGame(ptg.Message);
                        bFailure = true;
                        if (System.IO.File.Exists(szBillTicketInformation)) System.IO.File.Delete(szBillTicketInformation);//se sterge fisierul daca exista
                        //return;
                    }
                    if (IsBitSet(Buffer[3], 4))
                    {
                        if (bFirstTime)//daca s-a pornit prima data bill acceptorul dupa joc, si s-a dat stack
                        {
                            Logger.Write("Mei cashflow", " s-a intrat in conditia de prima pornire a aparatului");
                            bFirstTime = false;
                            timerFirstTime.Enabled = true;
                        }
                    }
                }
                // bFirstTime = true;
                if (Buffer[0] == 0x02 && (Buffer[2] == 0x70 || Buffer[2] == 0x71) && IsBitSet(Buffer[4], 4) && Buffer[28] == 0x03)
                {
                    Logger.Write("Mei cashflow", " s-a intrat in conditia de extended");
                    if (bFirstTime)//daca s-a pornit prima data bill acceptorul dupa joc, si s-a dat stack
                    {
                        Logger.Write("Mei cashflow", " s-a intrat in conditia de prima pornire a aparatului");
                        bFirstTime = false;

                        for (int i = 0; i <= BytesReadFromPerif; i++)
                            oByteStacking[i] = Buffer[i];
                        timerFirstTime.Enabled = true;
                        //return;
                    }
                }

                oEnableAllBill[2] = SetBit(oEnableAllBill[2], 0, bAck);
                WriteToPort(oEnableAllBill);
                ReadFromSerialPortInternal();
                Logger.Write("Buffer dupa Init Mei: ", Buffer, 0, BytesReadFromPerif);

                requestSoftwareVersion[2] = SetBit(requestSoftwareVersion[2], 0, bAck);
                WriteToPort(requestSoftwareVersion);
                ReadFromSerialPortInternal();
                byte[] temp = new byte[9];
                Array.Copy(Buffer, 3, temp, 0, 9);
                ReportSerialNumberAndVersion(temp);
                bAck = !bAck;
                requestSerialNumber[2] = SetBit(requestSerialNumber[2], 0, bAck);
                WriteToPort(requestSerialNumber);
                ReadFromSerialPortInternal();
                byte[] temp2 = new byte[20];
                Array.Copy(Buffer, 3, temp2, 0, 20);
                ReportSerialNumberAndVersion(temp2, 2);



                //if (bRunninFirstTime)//se verifica denominarea doar daca s-a pornit pentru prima data, altfel ramane la fel
                //{
                //    oNoteSet[2] = SetBit(oNoteSet[2], 0, !bAck);
                //    WriteToPort(oNoteSet);
                //    ReadFromSerialPort();
                //    BillsDenom bill = new BillsDenom();
                //    if (Buffer[11] == 0x45 && Buffer[12] == 0x55 && Buffer[13] == 0x52)//ii euro
                //    {
                //        int[] a = { 5, 10, 20, 50, 100, 200, 500 };//euro
                //        for (int i = 0; i < 7; i++)
                //        {
                //            if (IsBitSet(oEnableAllBill[3], (byte)i))
                //            {
                //                bill.iValue = a[i];
                //                bill.bIsEnabled = true;
                //                billDenom.Add(bill);
                //            }
                //        }
                //    }
                //    else
                //        if (Buffer[11] == 0x52 && Buffer[12] == 0x4F && Buffer[13] == 0x4C) //daca ii ron
                //        {
                //            int[] a = { 1, 5, 10, 50, 100, 200, 500 };//ron
                //            for (int i = 0; i < 7; i++)
                //            {
                //                if (IsBitSet(oEnableAllBill[3], (byte)i))
                //                {
                //                    bill.iValue = a[i];
                //                    bill.bIsEnabled = true;
                //                    billDenom.Add(bill);
                //                }
                //            }
                //        }
                //        else
                //            if (Buffer[11] == 0x48 && Buffer[12] == 0x55 && Buffer[13] == 0x46)//daca ii huf
                //            {
                //                int[] a = { 200, 500, 1000, 2000, 5000, 10000, 20000 };//huf
                //                for (int i = 0; i < 7; i++)
                //                {
                //                    if (IsBitSet(oEnableAllBill[3], (byte)i))
                //                    {
                //                        bill.iValue = a[i];
                //                        bill.bIsEnabled = true;
                //                        billDenom.Add(bill);
                //                    }
                //                }
                //            }
                //}
            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in InitMei", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }

        }

        private void ReportSerialNumberAndVersion(byte[] temp, int part = 1)
        {
            PerifToGame ptgVersion = new PerifToGame();
            ptgVersion.Cmd = CmdPerifToGame.BillAcceptor;
            ptgVersion.Status = 0x13;
            if (part == 2)
                ptgVersion.Status = 0x14;
            ptgVersion.Msg = temp;
            SendToGame(ptgVersion.Message);
        }
        protected override void WriteToPort(byte[] oBuffer)
        {
            try
            {
                CheckSum(oBuffer);
                WriteToPort(oBuffer, 20);
            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in WriteToPort", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }
        }

        protected override void Dispose()
        {
            try
            {
                EnabledElapsedFromTimer = false;
                bAck = !bAck;
                oInhibit[2] = SetBit(oInhibit[2], 0, bAck);
                WriteToPort(oInhibit);
                //throw new Exception("The method or operation is not implemented.");
            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in Dispose", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }
        }

        protected override void ResendLastCommand()
        {
            //throw new Exception("The method or operation is not implemented.");
        }

        protected override void GetStatusDeviceWithTimer()
        {
            try
            {
                if (isInhibit)
                {
                    oInhibit[2] = SetBit(oInhibit[2], 0, bAck);
                    bAck = !bAck;
                    WriteToPort(oInhibit);
                }
                else
                {
                    if (bAck)
                    {
                        oStatusRequest[2] = SetBit(oStatusRequest[2], 0, true);
                    }
                    else
                        oStatusRequest[2] = SetBit(oStatusRequest[2], 0, false);
                    WriteToPort(oStatusRequest);
                }
            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in GetStatusDeviceWithTimer", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }
        }

        private bool IsInDatabase(string szTicket)
        {

            MSSQL database = new MSSQL();

            database.ConnectionString = connectionString;
            if (!String.IsNullOrEmpty(database.ConnectionString))
            {
                object databaseObject = database.ExecuteScalar("select * from ticket where ticket='" + szTicket + "'");

                if (databaseObject == null)
                {
                    return false;
                }
            }
            else return false;

            return true;
        }
        private bool IsNotExpired(string szTicket)
        {
            MSSQL database = new MSSQL();
            database.ConnectionString = connectionString;
            MyRowList reader = database.ExecuteOneDataReader("select ticket,in_date,out_date,valid_days from ticket where ticket='" + szTicket + "'");

            if (reader == null)
                return false;
            try
            {
                Convert.ToDateTime(reader["out_date"].data);
                return false;
            }
            catch
            {
            }

            DateTime dt = (DateTime)reader["in_date"].data;
            dt = dt.AddDays(Convert.ToDouble(reader["valid_days"].data));
            if ((!(dt > (DateTime.Now))) && (reader["out_date"].data != null))
            {
                return false;
            }
            return true;
        }
        private decimal GetTicketValue(string szTicket)
        {

            MSSQL database = new MSSQL();

            database.ConnectionString = connectionString;
            if (!String.IsNullOrEmpty(database.ConnectionString))
            {
                object databaseObject = database.ExecuteScalar("select value from ticket where ticket='" + szTicket + "'");

                if (databaseObject == null)
                {
                    return 0;
                }
                else
                    return Convert.ToDecimal(databaseObject);
            }
            else return 0;
        }

        internal override bool IsEnabled()
        {
            return EnabledElapsedFromTimer;
        }


        private void DealWithStackedEvents()
        {
            try
            {
                bStacking = false;

                PerifToGame ptg = new PerifToGame();
                ptg.Cmd = CmdPerifToGame.BillAcceptor;
                if ((Buffer[11] == 0x45 && Buffer[12] == 0x55 && Buffer[13] == 0x52) || (Buffer[11] == 0x52 && Buffer[12] == 0x4F && Buffer[13] == 0x4C) || (Buffer[11] == 0x48 && Buffer[12] == 0x55 && Buffer[13] == 0x46))//daca ii euro, ron sau huf
                {
                    bBillAccepted = false;
                    byte[] oVal = new byte[8];
                    for (int i = 0; i < oVal.Length; i++)
                        oVal[i] = 0x30;//valoarea pt spatiu
                    double dBillValue = 0;
                    try
                    {
                        dBillValue = (int.Parse(Encoding.ASCII.GetString(Buffer, 14, 3))) * Math.Pow(10, int.Parse(Encoding.ASCII.GetString(Buffer, 18, 2)));
                    }
                    catch
                    {
                    }
                    string s = Convert.ToString(dBillValue);
                    byte[] test = Encoding.ASCII.GetBytes(s);
                    for (int i = test.Length - 1, j = oVal.Length - 1; i >= 0; i--, j--)
                        oVal[j] = test[i];

                    ptg.Status = 0x00;
                    ptg.Value = oVal;
                    Logger.Write("Bitu care tre convertit in ASCII: ", oVal);
                    SendToGame(ptg.Message);
                }
                else
                {

                    if (System.IO.File.Exists(szBillTicketInformation))
                    {
                        global::System.IO.FileStream sr = global::System.IO.File.OpenRead(szBillTicketInformation);
                        System.Runtime.Serialization.Formatters.Binary.BinaryFormatter bfRead = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                        BillTicketInformation bti = new BillTicketInformation();
                        bti = (BillTicketInformation)bfRead.Deserialize(sr);
                        sr.Close();
                        byte[] oMessage = new byte[18];
                        int i, j;
                        MSSQL database = new MSSQL();
                        database.ConnectionString = connectionString;

                        database.ExecuteNonQuery("update ticket set out_date='" + DateTime.Now.ToString() + "' where ticket='" + bti.lBillTicketValue.ToString() + "'");

                        float fValue = bti.fTicketValueFromDatabase;
                        string szValue = fValue.ToString("F2");
                        oMessage = new byte[15];
                        for (i = 0; i < oMessage.Length; i++)
                            oMessage[i] = 0x30;

                        for (i = szValue.Length - 1, j = oMessage.Length - 1; i >= 0; i--, j--)
                        {
                            oMessage[j] = Convert.ToByte(szValue[i]);
                        }
                        szValue = bti.lBillTicketValue.ToString();
                        for (i = szValue.Length - 1, j = oMessage.Length - 1; i >= 0; i--, j--)
                        {
                            oMessage[j] = Convert.ToByte(szValue[i]);
                        }
                        ptg.Msg = oMessage;
                        ptg.Status = 0x02;
                        SendToGame(ptg.Message);
                        System.IO.File.Delete(szBillTicketInformation);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in DealWithStackedEvents", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }
        }

        protected void DealWithStackedEvents(byte[] oBuffer)//pentru prima pornire dupa power down, cu mesaj de stacked
        {
            try
            {
                bStacking = false;
                Logger.Write("S-a intrat in stacked cu bufferul", oBuffer);
                PerifToGame ptg = new PerifToGame();
                ptg.Cmd = CmdPerifToGame.BillAcceptor;
                if ((oBuffer[11] == 0x45 && oBuffer[12] == 0x55 && oBuffer[13] == 0x52) || (oBuffer[11] == 0x52 && oBuffer[12] == 0x4F && oBuffer[13] == 0x4C) || (oBuffer[11] == 0x48 && oBuffer[12] == 0x55 && oBuffer[13] == 0x46))//daca ii euro, ron sau huf
                {
                    bBillAccepted = false;
                    byte[] oVal = new byte[8];
                    for (int i = 0; i < oVal.Length; i++)
                        oVal[i] = 0x30;//valoarea pt spatiu
                    double dBillValue = 0;
                    try
                    {
                        dBillValue = (int.Parse(Encoding.ASCII.GetString(oBuffer, 14, 3))) * Math.Pow(10, int.Parse(Encoding.ASCII.GetString(oBuffer, 18, 2)));
                    }
                    catch
                    {
                    }
                    string s = Convert.ToString(dBillValue);
                    byte[] test = Encoding.ASCII.GetBytes(s);
                    for (int i = test.Length - 1, j = oVal.Length - 1; i >= 0; i--, j--)
                        oVal[j] = test[i];

                    ptg.Status = 0x00;
                    ptg.Value = oVal;
                    Logger.Write("Bitu care tre convertit in ASCII: ", oVal);
                    SendToGame(ptg.Message);

                }
                else
                {
                    if (System.IO.File.Exists(szBillTicketInformation))
                    {
                        global::System.IO.FileStream sr = global::System.IO.File.OpenRead(szBillTicketInformation);
                        System.Runtime.Serialization.Formatters.Binary.BinaryFormatter bfRead = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                        BillTicketInformation bti = new BillTicketInformation();
                        bti = (BillTicketInformation)bfRead.Deserialize(sr);
                        sr.Close();
                        byte[] oMessage = new byte[18];
                        int i, j;
                        MSSQL database = new MSSQL();
                        database.ConnectionString = connectionString;

                        database.ExecuteNonQuery("update tickets set outdate='" + DateTime.Now.ToString() + "' where ticket='" + bti.lBillTicketValue.ToString() + "'");

                        float fValue = bti.fTicketValueFromDatabase;
                        string szValue = fValue.ToString("F2");
                        oMessage = new byte[15];
                        for (i = 0; i < oMessage.Length; i++)
                            oMessage[i] = 0x30;

                        for (i = szValue.Length - 1, j = oMessage.Length - 1; i >= 0; i--, j--)
                        {
                            oMessage[j] = Convert.ToByte(szValue[i]);
                        }
                        szValue = bti.lBillTicketValue.ToString();
                        for (i = szValue.Length - 1, j = oMessage.Length - 1; i >= 0; i--, j--)
                        {
                            oMessage[j] = Convert.ToByte(szValue[i]);
                        }
                        ptg.Msg = oMessage;
                        ptg.Status = 0x02;
                        SendToGame(ptg.Message);

                        System.IO.File.Delete(szBillTicketInformation);
                    }
                }
                for (int i = 0; i < oByteStacking.Length; i++)
                    oByteStacking[i] = 0x00;

            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in DealWithStackedEvents cu buffer", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }
        }

        private void CheckSum(byte[] oByte)
        {
            try
            {
                byte oChkSum = 0x00;
                int i;
                for (i = 1; i < oByte.Length - 2; i++)//oByte fara checksum byte
                    oChkSum = (byte)(oChkSum ^ oByte[i]);
                oByte[i + 1] = oChkSum;
            }
            catch (Exception ex)
            {
                Logger.Write("Exceptie in CheckSum", "Message:" + ex.Message + "\nStackTrace:" + ex.StackTrace + "\nInnerException:" + ex.InnerException);
            }
        }

        private bool ValidChkSum(byte[] oByte, byte oChkSum)//tests the message integrity 
        {
            byte oValid = 0x00;
            for (int i = 1; i < (int)oByte[1] - 2; i++)
                oValid = (byte)(oValid ^ oByte[i]);
            return oValid == oChkSum;
        }

        /// <summary>
        /// Changes the value of oBitNumber bit number from oMyByte, from 0 to 1 and 1 to 0
        /// </summary>
        /// <param name="oMyByte">the value of byte in wich a bit is verified if is set</param>
        /// <param name="oBitNumber">the position of bit that is verified if is set</param>
        /// <param name="val">bAck or a bool value</param>
        /// <returns>the value of the modified oMyByte</returns>
        private static byte SetBit(byte oMyByte, byte oBitNumber, bool val)
        {
            byte masca = 1;
            masca <<= oBitNumber;
            if (val == false)
                return (byte)(oMyByte & (~masca));
            return (byte)(oMyByte | masca);
        }

        /// <summary>
        /// Verifies if a bit is set in the byte with value oMyByte and bit number oBitNumber
        /// </summary>
        /// <param name="oMyByte">the value of byte in wich a bit is verified if is set</param>
        /// <param name="oBitNumber">the position of bit that is verified if is set</param>
        /// <returns>true if the bit is set else returns false</returns>
        private bool IsBitSet(byte oMyByte, byte oBitNumber)
        {
            return (oMyByte & (1 << oBitNumber)) != 0;
        }

        #endregion

    }

    internal static class Logger
    {
        static GeneralLogger logger = new GeneralLogger();

        static Logger()
        {
            #region Log Settings
            logger.MainDirectoryPath = "C:\\Log\\PeripheralsCommunication\\";
            bool loggerEnabled = false;

            logger.Informations.WriteWhenCompiledInDebug = loggerEnabled;
            logger.Informations.WriteWhenCompiledInRelease = loggerEnabled;
            logger.Warnings.WriteWhenCompiledInDebug = loggerEnabled;
            logger.Warnings.WriteWhenCompiledInRelease = loggerEnabled;
            logger.Incomings.WriteWhenCompiledInDebug = loggerEnabled;
            logger.Incomings.WriteWhenCompiledInRelease = loggerEnabled;
            logger.Outgoings.WriteWhenCompiledInDebug = loggerEnabled;
            logger.Outgoings.WriteWhenCompiledInRelease = loggerEnabled;
            logger.Errors.WriteWhenCompiledInDebug = loggerEnabled;
            logger.Errors.WriteWhenCompiledInRelease = loggerEnabled;
            logger.FatalErrors.WriteWhenCompiledInDebug = loggerEnabled;
            logger.FatalErrors.WriteWhenCompiledInRelease = loggerEnabled;
            #endregion
        }

        public static void Write(string szDescription, byte[] oToWrite, int iOffset, int iLength)
        {
            logger.Errors.Write(szDescription, oToWrite, iOffset, iLength);
        }

        /// <summary>
        /// this function writes in current log file an array of bytes and his description
        /// </summary>
        /// <param name="szDescription">the description of the array</param>
        /// <param name="oToWrite">the array that is writen</param>
        public static void Write(string szDescription, byte[] oToWrite)
        {
            logger.Errors.Write(szDescription, oToWrite);
        }

        /// <summary>
        /// this function writes in current log file two string first is the description and second string is the detailed information 
        /// </summary>
        /// <param name="szDescription">the description</param>
        /// <param name="szToWrite">the detailed information</param>
        public static void Write(string szDescription, string szToWrite)
        {
            logger.Errors.Write(szDescription, szToWrite);
        }
    }

    internal abstract class PerifericsNormal
    {
        protected bool isInhibit = true;
        /// <summary>
        /// the values used for the status part in the change status command
        /// </summary>
        public enum ChangeStatusStatus : byte
        {
            On = 0x00,   //turns on the device
            Blink = 0x50,//only for Infinity lamp
            Off = 0xFF   //turns off the device
        }

        /// <summary>
        /// the values used for the value part in the change status command
        /// </summary>
        public enum ChangeStatusVals : int
        {
            RedLight = 0x0001,        //for red light
            GreenLight = 0x0002,      //for green light
            YellowLight = 0x0004,     //for yallow light
            StartButtonLight = 0x0008,//for start button light
            CoinAcceptor = 0x0010,    //for coin acceptor
            RFIDCard = 0x0040,        //for the RFID card
            ServiceMode = 0x0080,     //for service mode
            InfinityLamp = 0x0100     //for Infinity lamp          
        }

        #region Variables and constants used in class
        /// <summary>
        /// retains true if the DataRecived event from serial port is overriden(if enabled it automtically reads from serial port and call function PrelucrateFromPerifericsToGame)
        /// </summary>
        private bool bEnabledDataRecivedFromSerial;

        /// <summary>
        /// retains true if the Elapsed event from System.Timer is overriden(if enabled it automatically calls GetStatusDeviceWithTimer and PrelucrateFromPerifericsToGame
        /// </summary>
        private bool bEnabledElapsedFromTimer;

        /// <summary>
        /// the timer that is used for reading from the serial port
        /// this is used only for periferics that need to read from the port at specified intervals
        /// </summary>
        private System.Timers.Timer timer;

        /// <summary>
        /// data read from periferic
        /// </summary>
        private byte[] oBuffer = new byte[1024];

        /// <summary>
        /// retains the last command sent to game(the instante of the class have no importance(static member)
        /// </summary>
        private static byte[] oLastCommand;

        /// <summary>
        /// number of bytes read form periferic 
        /// </summary>
        private int iBytesReadFromPerif;

        /// <summary>
        /// if you want to resend the last command to periferic when a Timeout exception appeared this member must be true
        /// </summary>
        private bool bResendLastCommandWhenTimeout;

        public bool stackerOpenSent = false;
        /// <summary>
        /// serial port for the periferic
        /// </summary>
        private SerialPort sp;

        /// <summary>
        /// reatains true is a Timeout exception is raised
        /// </summary>
        private bool bIsTimout;

        /// <summary>
        /// retains true if the class is disposed
        /// </summary>
        private bool bIsDisposed;

        /// <summary>
        /// retains the connection string to database
        /// </summary>

        protected IniFile ini = new IniFile("C:\\Program Files\\Softparc\\config.ini");
        protected string connectionString;

        #endregion

        #region Delegates and events defined in class
        protected delegate void TimeoutDelegate(object sender, PerifericTimeoutEvent e);

        /// <summary>
        /// this event occurs when a Timeout exception is raised
        /// </summary>
        protected event TimeoutDelegate OnTimeout;

        protected delegate void TimeoutRecoverDelegate(object sender, PerifericTimeoutRecoverEvent e);

        /// <summary>
        /// this event occurs when communication was reastibleshed with the equipment
        /// </summary>
        protected event TimeoutRecoverDelegate OnRecoverTimeout;

        protected delegate void DataRecivedFromPerifercDelegate(object sender, PerifericDataRecivedFromPerifericEvent e);

        /// <summary>
        /// this event is raised when the event DataRecived from serial port is raised
        /// </summary>
        protected event DataRecivedFromPerifercDelegate OnDataRecivedFromPeriferic;

        protected delegate void DataRecivedFromPerifericTimerDelegate(object sender, PerifericDataRecivedFromPerifericTimerEvent e);

        /// <summary>
        /// this evend is raised when data was recived because a Status request command was sent to periferic using the timer to send this command at regular intervals
        /// </summary>
        protected event DataRecivedFromPerifericTimerDelegate OnDataRecivedFromPerifericTimer;

        protected delegate void DataRecivedFromGameDelegate(object sender, PerifericDataRecivedFromGameEvent e);

        /// <summary>
        /// this event is raised when new data is recived from game(a command is sent to periferic)
        /// </summary>
        protected event DataRecivedFromGameDelegate OnDataRecivedFromGame;
        protected event EventHandler<BillStackedEventArgs> OnBillStacked;

        #endregion

        #region Constructors of the class
        /// <summary>
        /// the constructor of class needed to initialize the class in this form the timer is not used
        /// </summary>
        /// <param name="sp">the serial port used to communicate</param>        
        public PerifericsNormal(SerialPort sp)
        {
            this.sp = sp;
            try
            {
                connectionString = ini.IniReadValue("ConnectionStrings", "Game");

                this.sp.Open();
                this.sp.DataReceived += new SerialDataReceivedEventHandler(sp_DataReceived);
            }
            catch (Exception e)
            {
                Logger.Write("The " + this.sp.PortName + " cannot be open in constructor of class", "Exception message:" + e.Message + " StackTrace:" + e.StackTrace);
            }

            this.bEnabledDataRecivedFromSerial = true;

            this.bEnabledElapsedFromTimer = false;
        }

        /// <summary>
        /// the constructor of class - in this form the timer is used
        /// </summary>
        /// <param name="sp">the serial port that is used to comunicate with the periferic</param>        
        /// <param name="timer">the timer that is used to read from periferics</param>
        public PerifericsNormal(SerialPort sp, System.Timers.Timer timer)
        {

            this.sp = sp;
            this.EnableDataRecivedFromSerial = true;
            this.EnableDataRecivedFromSerial = false;
            this.timer = timer;
            try
            {
                connectionString = ini.IniReadValue("ConnectionStrings", "Game");

                this.sp.Open();
                this.timer.Elapsed += new System.Timers.ElapsedEventHandler(timer_Elapsed);
            }
            catch (Exception e)
            {
                Logger.Write("Cannot open " + this.sp.PortName + " in constructor of class Periferic", "The exception message:" + e.Message + " StackTrace:" + e.StackTrace);
            }

            this.bEnabledElapsedFromTimer = true;
            this.EnabledElapsedFromTimer = false;
            this.timer.Enabled = true;
        }
        private bool ValidChkSum(byte[] oByte, byte oChkSum)//tests the message integrity 
        {
            byte oValid = 0x00;
            for (int i = 1; i < (int)oByte[1] - 2; i++)
                oValid = (byte)(oValid ^ oByte[i]);
            return oValid == oChkSum;
        }
        private static byte SetBit(byte oMyByte, byte oBitNumber, bool val)
        {
            byte masca = 1;
            masca <<= oBitNumber;
            if (val == false)
                return (byte)(oMyByte & (~masca));
            return (byte)(oMyByte | masca);
        }

        #endregion

        #region Functions used to write to serial port(the COM port)
        /// <summary>
        /// writes to the port the specified buffer
        /// </summary>
        /// <param name="oBuffer">the buffer that is written to COM port and then sleeps 200 milisecs</param>
        protected virtual void WriteToPort(byte[] oBuffer)
        {
            WriteToPort(oBuffer, 0, oBuffer.Length, 200);//era 200 la ultima inainte
        }

        /// <summary>
        /// writes to the COM port the specified buffer and than sleeps 
        /// </summary>
        /// <param name="oBuffer">the buffer that is written</param>
        /// <param name="iSleep">the number of milliseconds when the current thread sleeps</param>
        protected void WriteToPort(byte[] oBuffer, int iSleep)
        {
            WriteToPort(oBuffer, 0, oBuffer.Length, iSleep);
        }

        /// <summary>
        /// writes to the COM port the specifier buffer from a offset and with a specified length
        /// </summary>
        /// <param name="oBuffer">the buffer that is written to the serial port</param>
        /// <param name="iOffset">the offset from where the writting begins</param>
        /// <param name="iLength">the number of bytes to write</param>
        /// <param name="iSleep">the number of miliseconds that the thread will sleep</param>
        protected void WriteToPort(byte[] oBuffer, int iOffset, int iLength, int iSleep)
        {
            //if (isInhibit)
            //    return;
            try
            {
                sp.Write(oBuffer, iOffset, iLength);
            }
            catch (Exception e)
            {
                Type t = this.GetType();
                Logger.Write("in write to port", "Eroarea:" + e.Message + " in clasa " + t.Name);
            }
            //PerifericsLogger.Write("Write from class type " + GetType().ToString() + " an obj of type GameToPerif", oBuffer, iOffset, iLength); 
            Thread.Sleep(iSleep);
        }
        #endregion
        bool sendNotFoundOnce = true;
        #region Functions used to read from serial port(COM ports)
        /// <summary>
        /// function used to read from the serial port; if nothing is read than automatically resends the last command to serial port
        /// </summary>
        /// <param name="iSleep">the number of milliseconds that the current thread will sleep after the data is read</param>
        protected void ReadFromSerialPort(int iSleep)
        {
            //reading from the port in oBuffer
            //iBytesReadFromPerif - numeber of bytes read from the port
            int iCount = 0, iReadBytes = 0;
            iBytesReadFromPerif = 0;
            for (iCount = 0; iCount < oBuffer.Length; iCount++)
                oBuffer[iCount] = 0;

            //reads the bytes from the serial port
            try
            {
                do
                {
                    iReadBytes = sp.Read(oBuffer, iBytesReadFromPerif, oBuffer.Length - iBytesReadFromPerif);
                    Thread.Sleep(iSleep);
                    iBytesReadFromPerif += iReadBytes;
                } while (sp.BytesToRead > 0);
                IsTimeout = false;
                if (!sendNotFoundOnce)
                {
                    sendNotFoundOnce = true;
                    PerifToGame ptg = new PerifToGame();
                    ptg.Status = 0xFB;
                    ptg.Cmd = CmdPerifToGame.BillAcceptor;
                    SendCmdToGame(ptg, EventArgs.Empty);
                }
            }
            //if no byte were read resends the last command to periferic
            catch (TimeoutException te)
            {
                //IsTimeout = true;
                if (sendNotFoundOnce)
                {
                    sendNotFoundOnce = false;
                    PerifToGame ptg = new PerifToGame();
                    ptg.Status = 0xFA;
                    ptg.Cmd = CmdPerifToGame.BillAcceptor;
                    SendCmdToGame(ptg, EventArgs.Empty);
                }
                if (bResendLastCommandWhenTimeout)
                {
                    Logger.Write("Because was a timeout exception and cannot read from serial port the last command is resend", "StackTrace:" + te.StackTrace);
                    ResendLastCommand();
                }
                return;
            }
            catch (Exception e)
            {
                Logger.Write("A exception has occured in base clsss", "The exception:" + e.Message + " StackTrace:" + e.StackTrace);
            }
        }
        private bool IsBitSet(byte oMyByte, byte oBitNumber)
        {
            return (oMyByte & (1 << oBitNumber)) != 0;
        }
        bool trimite = true;
        /// <summary>
        /// reads from COM port and than sleeps 50 miliseconds
        /// </summary>
        protected void ReadFromSerialPort()
        {
            ReadFromSerialPort(50);
            //if (IsBitSet(Buffer[4], 4) && !bStackerOpen && !acceptatAici) //daca nu ii mesaj de stack dupa repunerea stackerului la loc
            if (Buffer[4] == 0x11 || Buffer[3] == 0x11)
            {
                //Logger.Write("Byte ------------------ a intrat in Stack", Buffer, 0, BytesReadFromPerif);
                //if (!timerFirstTime.Enabled)
                if (Buffer[3] == 0x02)
                    if (OnBillStacked != null)
                        if (trimite)
                        {
                            OnBillStacked(this, new BillStackedEventArgs(Buffer));
                            trimite = false;
                            Thread t = new Thread(() =>
                            {
                                Thread.Sleep(1500);
                                trimite = true;
                            });
                            t.Start();
                        }
            }
            if (!(Buffer[2] == 0x70 || Buffer[2] == 0x71))//testeaza daca este mesaj extended sau nu
                if (!IsBitSet(Buffer[5], 1) && Buffer[0] == 0x02 && Buffer[9] == 0x03)//if a valid response was received from acceptor
                    if (ValidChkSum(Buffer, Buffer[10]))
                    {
                        if (!IsBitSet(Buffer[4], 4)) //daca s-a scos stackerul si bStackerOpen=false-pt a nu retransmite mesajul
                        {
                            if (!stackerOpenSent)
                            {
                                stackerOpenSent = true;
                                PerifToGame ptg = new PerifToGame();
                                ptg.Cmd = CmdPerifToGame.BillAcceptor;
                                Logger.Write("Mei Cashflow", "S-a scos Stackerul!!!!");
                                ptg.Status = 0xF9;
                                byte[] oMsg = new byte[8];
                                oMsg[3] = SetBit(oMsg[3], 1, true);
                                ptg.Msg = oMsg;
                                SendToGame(ptg.Message);
                                return;
                            }
                        }
                    }
        }
        #endregion

        #region Functions used to send messages to the game
        /// <summary>
        /// sends an array of bytes using socket member sk 
        /// </summary>
        /// <param name="oMessage">the message that is sent</param>
        /// <param name="iOffset">the offset</param>
        /// <param name="iLength">the length</param>
        protected void SendToGame(byte[] oMessage, int iOffset, int iLength)
        {
            PerifToGame ptg = new PerifToGame();
            ptg.Message = oMessage;

            if (SendCmdToGame != null)
            {
                Logger.Write("Send from GameIO to game the message", oMessage, iOffset, iLength);
                SendCmdToGame(ptg, new EventArgs());
            }
            else
                Logger.Write("Send to game e null", "abc");


            oLastCommand = oMessage;
        }

        /// <summary>
        /// sends an array of bytes using socket member sk
        /// </summary>
        /// <param name="oMessage">the message that is sent</param>
        protected void SendToGame(byte[] oMessage)
        {
            SendToGame(oMessage, 0, oMessage.Length);
        }

        #endregion

        #region Function used to verify if two vectors are equal
        /// <summary>
        /// function that verifies if two commands are equal
        /// </summary>
        /// <param name="oCommand">first command</param>
        /// <param name="oRecivedCommand">second command</param>
        /// <returns>true if the commands are equal</returns>
        protected bool AreEqual(byte[] oCommand, byte[] oRecivedCommand)
        {
            for (int iCount = 0; iCount < oRecivedCommand.Length; iCount++)
                if (oCommand[iCount] != oRecivedCommand[iCount])
                    return false;
            return true;
        }

        /// <summary>
        /// check if oState is equal with Buffer
        /// </summary>
        /// <param name="oState">the parameter wich retains the state to be checked</param>
        /// <returns>true is the content of Buffer and oState is equal</returns>
        protected bool IsInState(byte[] oState)
        {
            return AreEqual(Buffer, oState);
        }
        #endregion

        #region Proprieties defined in class
        /// <summary>
        /// calls events OnTimeout si OnRecoverTimeout
        /// </summary>
        private bool IsTimeout
        {
            get { return bIsTimout; }
            set
            {
                if (value == bIsTimout) return;
                bIsTimout = value;
                if (OnTimeout != null)
                    if (bIsTimout)
                    {
                        OnTimeout(this, new PerifericTimeoutEvent());
                        System.Threading.Thread.Sleep(30000);
                    }
                    else
                        if (OnRecoverTimeout != null)
                        {
                            System.Threading.Thread.Sleep(30000);
                            OnRecoverTimeout(this, new PerifericTimeoutRecoverEvent());
                        }
            }
        }

        /// <summary>
        /// returns true if is timeout else false(get;)
        /// </summary>
        protected bool Timeout
        {
            get
            {
                return bIsTimout;
            }
        }

        /// <summary>
        /// returns the array of read bytes from the serial port(get;)
        /// </summary>
        protected byte[] Buffer
        {
            get
            {
                return (byte[])oBuffer.Clone();
            }
        }

        /// <summary>
        /// returns the serial port with wich is communicating(get;)
        /// </summary>
        protected SerialPort SerPrt
        {
            get
            {
                return sp;
            }
        }

        /// <summary>
        /// return the number of bytes thatr were read from the serial port(get;)
        /// </summary>
        protected int BytesReadFromPerif
        {
            get
            {
                return iBytesReadFromPerif;
            }
        }

        /// <summary>
        /// gets the last command that was send to game (get;)
        /// </summary>
        public static byte[] LastCommand
        {
            get
            {
                return oLastCommand;
            }
            set
            {
                oLastCommand = value;
            }
        }

        /// <summary>
        /// enable or disable the DataRecived event from the serial port 
        /// this is set automatically to true when the constructor without the timer parmeter is used (get;set)
        /// </summary>
        protected bool EnableDataRecivedFromSerial
        {
            get
            {
                return bEnabledDataRecivedFromSerial;
            }

            set
            {
                if (value == bEnabledDataRecivedFromSerial) return;
                if (value == true) sp.DataReceived += new SerialDataReceivedEventHandler(sp_DataReceived);
                else sp.DataReceived -= sp_DataReceived;
                bEnabledDataRecivedFromSerial = value;
            }
        }

        /// <summary>
        /// enable or disable the timer event Elapsed in wich data is read from serial port at a specified interval
        /// this is set automatically to true when the constructor with timer parameter is used (get;set)
        /// </summary>
        protected bool EnabledElapsedFromTimer
        {
            get
            {
                return bEnabledElapsedFromTimer;
            }

            set
            {
                if (bEnabledElapsedFromTimer == value) return;
                if (value == true)
                    if (timer == null) throw new PerifericsException("The timer is null. Cannot override event System.Timers.Timer.Elaspsed. Use the constructor with timer parameter");
                    else
                    {
                        if (!IsTimeout)
                        {
                            timer.Enabled = true;
                            //timer.Elapsed += new System.Timers.ElapsedEventHandler(timer_Elapsed);
                            Logger.Write("timer enabled", "in class of type " + this.GetType().Name);
                        }
                    }
                else
                {
                    timer.Enabled = false;
                    //timer.Elapsed -= timer_Elapsed;
                    Logger.Write("timer disabled", "in class of type " + this.GetType().Name);
                }
                bEnabledElapsedFromTimer = value;
            }
        }

        /// <summary>
        /// if this is true than the function ResendLastCommand is automatically used when a Timeout exception occured (get;set)
        /// </summary>
        protected bool ResendLastCommandWhenTimeout
        {
            get
            {
                return bResendLastCommandWhenTimeout;
            }

            set
            {
                bResendLastCommandWhenTimeout = value;
            }
        }
        #endregion

        #region Abstract or virtual functions
        /// <summary>
        /// virtual function used to send to periferic a command with the host ask the status of the device
        /// this function must be overriden for every device that use a timer to check the device status
        /// </summary>
        protected virtual void GetStatusDeviceWithTimer()
        {
        }

        internal virtual bool IsEnabled()
        {
            return true;
        }

        /// <summary>
        /// abstract function used to resend the last command that was send to a periferic
        /// this function is used when a periferic I/O error was detected
        /// </summary>
        protected abstract void ResendLastCommand();

        /// <summary>
        /// function used to release the resource that the periferic was using;
        /// the connection with the ports are closed automatically after this function was used
        /// </summary>
        protected abstract void Dispose();
        #endregion

        #region Other functions used in class

        /// <summary>
        /// function used to interceptate message DataRecived and read bytes read from the port
        /// </summary>
        private void sp_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            ReadFromSerialPort();
            if (OnDataRecivedFromPeriferic != null && BytesReadFromPerif > 0)
                OnDataRecivedFromPeriferic(this, new PerifericDataRecivedFromPerifericEvent());
        }

        /// <summary>
        /// function used to read from the port at a specified interval
        /// </summary>
        void timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {

            GetStatusDeviceWithTimer();
            ReadFromSerialPort();
            if (isInhibit)
                return;
            timer.Enabled = false;
            if (OnDataRecivedFromPerifericTimer != null && BytesReadFromPerif > 0)
                OnDataRecivedFromPerifericTimer(this, new PerifericDataRecivedFromPerifericTimerEvent());
            timer.Enabled = true;

        }

        /// <summary>
        /// function used to make a periferic to execute a command using the message recived from the parameter
        /// </summary>
        /// <param name="gp">the message that is recived from the game</param>
        public void ExecuteMessage(GameToPerif gp)
        {
            if (OnDataRecivedFromGame != null && !bIsDisposed)//if the event is defined and the class is not disposed
                OnDataRecivedFromGame(this, new PerifericDataRecivedFromGameEvent(gp));
        }

        /// <summary>
        /// use this function to dispose any resource the periferic was using execept the Socket
        /// </summary>
        public void DisposePeriferic()
        {
            bIsDisposed = true;
            Dispose();
            sp.Close();
        }
        #endregion

        public virtual void Initialize()
        {
        }

        public event EventHandler<EventArgs> SendCmdToGame;

    }
    public class BillStackedEventArgs : EventArgs
    {
        public byte[] buffer;
        public BillStackedEventArgs(byte[] buff)
        {
            buffer = buff;
        }
    }
    internal class GameToPerif
    {
        byte[] oMessage = new byte[256];

        /// <summary>
        /// command sent by game (get;set)-1 byte
        /// </summary>
        public CmdGameToPerif Cmd
        {
            get
            {
                return (CmdGameToPerif)oMessage[0];
            }
            set
            {
                oMessage[0] = (byte)value;
            }
        }

        /// <summary>
        /// value sent by the game (get;set)- 2 bytes
        /// </summary>
        public byte[] ValueBytes
        {
            get
            {
                byte[] oRetur = new byte[2];
                oRetur[0] = oMessage[1];
                oRetur[1] = oMessage[2];
                return oRetur;
            }

            set
            {
                oMessage[1] = value[0];
                oMessage[2] = value[1];
            }
        }

        public int Value
        {
            get
            {
                return oMessage[1] * 256 + oMessage[2];
            }

            set
            {
                oMessage[1] = Convert.ToByte(value / 256);
                oMessage[2] = Convert.ToByte(value % 256);
            }
        }


        /// <summary>
        /// status of the command (get;set)- 1 byte
        /// </summary>
        public byte Status
        {
            get
            {
                return oMessage[3];
            }

            set
            {
                oMessage[3] = value;
            }
        }

        /// <summary>
        /// additional information about the command (get;set)- 11 bytes
        /// </summary>
        public byte[] Msg
        {
            get
            {
                byte[] oRetur = new byte[251];
                for (int iCounter = 0; iCounter < 251; iCounter++)
                    oRetur[iCounter] = oMessage[iCounter + 4];
                return oRetur;
            }

            set
            {
                for (int iCounter = 0; iCounter < value.Length; iCounter++)
                    oMessage[iCounter + 4] = value[iCounter];
            }
        }

        /// <summary>
        /// length of the message from the Msg field
        /// </summary>
        public byte Msglen
        {
            get
            {
                return oMessage[255];
            }
            set
            {
                oMessage[255] = value;
            }
        }

        public byte this[int iIndex]
        {
            get
            {
                return oMessage[iIndex];
            }

            set
            {
                oMessage[iIndex] = value;
            }
        }

        /// <summary>
        /// the whole message (get;set)- 16 bytes
        /// </summary>
        public byte[] Message
        {
            get
            {
                return (byte[])oMessage.Clone();
            }

            set
            {
                oMessage = (byte[])value.Clone();
            }
        }
    }

    public class PerifericTimeoutEvent : EventArgs
    {
    }

    /// <summary>
    /// 
    /// </summary>
    public class PerifericTimeoutRecoverEvent : EventArgs
    {
    }

    /// <summary>
    /// 
    /// </summary>
    public class PerifericDataRecivedFromPerifericTimerEvent : EventArgs
    {
    }

    /// <summary>
    /// 
    /// </summary>
    public class PerifericDataRecivedFromPerifericEvent : EventArgs
    {
    }

    /// <summary>
    /// 
    /// </summary>
    internal class PerifericDataRecivedFromGameEvent : EventArgs
    {
        GameToPerif gtp;
        /// <summary>
        /// 
        /// </summary>
        /// <param name="gp"></param>
        public PerifericDataRecivedFromGameEvent(GameToPerif gp)
        {
            this.gtp = gp;
        }

        /// <summary>
        /// 
        /// </summary>
        public GameToPerif GameToPerifericData
        {
            get { return gtp; }
        }
    }

    /// <summary>
    /// 
    /// </summary>
    public class PerifericADoorIsOpenEvent : EventArgs
    {
    }

    /// <summary>
    /// 
    /// </summary>
    public class PerifericAllDoorClosedEvent : EventArgs
    {
    }

    internal enum CmdGameToPerif : byte
    {
        oTest = 0x00,//doar pentru test, simularea mesajelor primite de la port
        oCounter = 0x01,//counter
        oChangeStatus = 0x02,//change status perif
        oHopper = 0x03,//hopper
        oStatusRequest = 0x04,//status request
        oPrinter = 0x05,//printer
        oAcceptBillTicket = 0x06,//command for accepting bill or ticket
        oGsm = 0x07,//gsm device
        oIgnoreDoorsState = 0x08,//ignore or not the state of doors
        oBillVersion = 0x09,
        oRejecttBillTicket = 0x10,
        oError = 0xFF//a error
    }
    internal enum CmdPerifToGame : byte
    {
        Test = 0x00,             //doar pt test IOBOARD nou   
        BillAcceptor = 0x01,   // BillAceptor        
        CoinAcceptor = 0x02,   //CoinAcceptor
        RFIDCard = 0x03,       //RFID Card
        StartButton = 0x04,    //Start button
        DoorOpen = 0x05,       //Door open
        Hopper = 0x06,         //Hopper
        Printer = 0x07,        //Printer
        Version = 0x08,        //Version info
        NoComPorts = 0x09,     //No com port(s) found
        GSM = 0x10,            //GSM device
        ExitServiceMode = 0x011, //Exit service mode
        KeySwitchOpen = 0x12,  //KeySwitch open
        Heartbeat = 0x13,/*************************************************************** test heartbeat ****************/
        Error = 0xFF           //An error
    }
    internal class PerifToGame
    {
        byte[] oMessage = new byte[256];

        /// <summary>
        /// command sent by the periferics(get;set)-1 byte
        /// </summary>
        public CmdPerifToGame Cmd
        {
            get
            {
                return (CmdPerifToGame)oMessage[0];
            }

            set
            {
                oMessage[0] = (byte)value;
            }
        }

        /// <summary>
        /// value sent by periferics(get;set)-8 bytes
        /// </summary>
        public byte[] Value
        {
            get
            {
                byte[] oRetur = new byte[8];
                for (int iCount = 0; iCount < 8; iCount++)
                    oRetur[iCount] = oMessage[iCount + 1];
                return oRetur;
            }

            set
            {
                for (int iCount = 0; iCount < value.Length; iCount++)
                    oMessage[iCount + 1] = value[iCount];
            }
        }

        /// <summary>
        /// status of the command(get;set)-1 byte
        /// </summary>
        public byte Status
        {
            get
            {
                return oMessage[9];
            }

            set
            {
                oMessage[9] = value;
            }
        }

        /// <summary>
        /// length of Msg field maximum is 245(get;set)-1 byte
        /// </summary>
        public byte MsgLen
        {
            get
            {
                return oMessage[10];
            }

            set
            {
                oMessage[10] = value;
            }
        }

        /// <summary>
        /// additional information associated with the command(get;set)-245 bytes
        /// </summary>
        public byte[] Msg
        {
            get
            {
                byte[] oRetur = new byte[245];
                for (int iCount = 0; iCount < 244; iCount++)
                    oRetur[iCount] = oMessage[iCount + 11];
                return oRetur;
            }

            set
            {
                for (int iCount = 0; iCount < value.Length; iCount++)
                    oMessage[iCount + 11] = value[iCount];
            }
        }

        public byte this[int iIndex]
        {
            get
            {
                return oMessage[iIndex];
            }

            set
            {
                oMessage[iIndex] = value;
            }
        }

        /// <summary>
        /// the whole message with Cmd, Value, Status, MsgLen and Msg(get;set)- 256 bytes
        /// </summary>
        public byte[] Message
        {
            get
            {
                return (byte[])oMessage.Clone();
            }

            set
            {
                oMessage = (byte[])value.Clone();
            }
        }
    }
    class PerifericsException : ApplicationException
    {
        public PerifericsException(string szException)
            : base(szException)
        {
            Logger.Write("Periferics exception", szException);
        }
    }
}

