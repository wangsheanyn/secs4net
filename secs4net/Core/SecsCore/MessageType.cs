namespace Secs4Net {
    enum MessageType : byte {
        DataMessage         = 0b00000000,   //
        SelectRequest       = 0b00000001,   //	ReplyExpected
        SelectResponse      = 0b00000010,   //
        //Deselect_req      = 0b00000011,   //	ReplyExpected
        //Deselect_rsp      = 0b00000100,   //
        LinkTestRequest     = 0b00000101,   //	ReplyExpected
        LinkTestResponse    = 0b00000110,   //
        //Reject_req        = 0b00000111,   //
        SeperateRequest     = 0b00001001,   //
    }
}