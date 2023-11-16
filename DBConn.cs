using System.Data.SqlClient

namespace DBConn
public class DBConn
{
    private string connection_string = "Data Source=ip,port;Initial Catalog=input_database_default_schema_name;UID=input_user_id;Pwd=input_pwd;";
    // Select
    private SqlConnection conn = new SqlConnection();
    // Insert, Update, Delete
    private SqlConnection conn2 = new SqlConnection();
    private SqlDataReader dr;

    private bool DBOpen()
    {
        try
        {
            conn.ConnectionString = connection_string;
            conn2.ConnectionString = connection_string;

            conn.Open();
            conn2.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool DBClose()
    {
        try
        {
            conn.Close();
            conn2.Close();

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool OpenRs(string query, SqlCommand cmd = null)
    {
        if (cmd == null)
        {
            if (dr != null)
            {
                CloseRs();
            }
            SqlCommand _cmd = new SqlCommand
            {
                CommandText = query,
                connection = conn
            };
            try
            {
                if (conn.State == ConnectionState.Open)
                {
                    dr = cmd.ExecuteReader();
                    return true;
                }
                else
                {
                    DBClose();
                    DBOpen();
                    SqlCommand _cmd2 = new SqlCommand
                    {
                        CommandText = query,
                        connection = conn
                    };
                    dr = _cmd2.ExecuteReader();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
        else
        {
            SqlCommand _cmd3 = cmd;
            _cmd3.Connection = conn;
            try
            {
                _cmd3.ExecuteReader();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool CloseRs ()
    {
        try
        {
            if (dr != null)
            {
                dr.Close();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Execute(string query, SqlCommand cmd = null)
    {
        if (cmd == null) {
            SqlCommand _cmd4 = new SqlCommand{ CommandText = query, Connection = conn2 };
            try
            {
                if (conn2.State == ConnectionState.Open)
                {
                    _cmd4.ExecuteNonQuery();
                    return true;
                }
                else
                {
                    DBClose();
                    DBOpen();
                    SqlCommand _cmd5 = new SqlCommand{ CommandText = query, Connection = conn2 };
                    _cmd5.ExecuteNonQuery();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
        else
        {
            SqlCommand _cmd6 = cmd;
            _cmd6.Connection = conn2;
            try
            {
                if (conn2.State == ConnectionState.Open)
                {
                    _cmd6.ExecuteNonQuery();
                    return true;
                }
                else
                {
                    DBClose();
                    DBOpen();
                    SqlCommand _cmd7 = cmd;
                    _cmd7.Connection = conn2;
                    _cmd7.ExecuteNonQuery();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}